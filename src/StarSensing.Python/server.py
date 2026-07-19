import grpc
from concurrent import futures
import time
import logging
import yaml

import protos.star_sensing_pb2 as pb2
import protos.star_sensing_pb2_grpc as pb2_grpc

from signal_processor import SignalProcessor
from motion_detector import MotionDetector
from anomaly_detector import AnomalyDetector
from spatial_inference import SpatialInferencer
from feature_extractor import enrich_signals, stability_index
from lstm_predictor import LstmMotionPredictor
from cnn_heatmap import CnnHeatmapAnalyzer
from bearing_store import BearingStore
from map_geometry import positions_from_signals

logging.basicConfig(level=logging.INFO, format='%(asctime)s [%(levelname)s] %(name)s: %(message)s')
logger = logging.getLogger("StarSensing.Python")


def _classify(motion_conf: float) -> int:
    if motion_conf >= 0.8:
        return pb2.HIGH_ACTIVITY
    if motion_conf >= 0.5:
        return pb2.MODERATE_ACTIVITY
    if motion_conf >= 0.2:
        return pb2.LOW_ACTIVITY
    if motion_conf > 0.05:
        return pb2.TRANSITION
    return pb2.STATIC


class SignalProcessorService(pb2_grpc.SignalProcessorServiceServicer):
    def __init__(self, config):
        self.config = config
        sample_rate = config.get('sample_rate_hz', 10.0)
        models_dir = config.get('models_dir', 'models')
        sql_cs = config.get('sql_connection_string')
        map_radius = float(config.get('map_radius_m', 30.0))
        bearing_refresh = float(config.get('bearing_refresh_sec', 60.0))

        self.processor = SignalProcessor(
            window_size=config.get('window_size', 10),
            sample_rate=sample_rate,
        )
        self.motion_detector = MotionDetector()
        self.anomaly_detector = AnomalyDetector()
        self.spatial = SpatialInferencer(models_dir)
        self.lstm = LstmMotionPredictor(models_dir)
        self.cnn = CnnHeatmapAnalyzer(models_dir)
        self._bearing_store = BearingStore(
            connection_string=sql_cs,
            refresh_interval_sec=bearing_refresh,
            map_radius_m=map_radius,
        )

    def _map_positions(self, request, processed: list[dict]) -> dict[str, tuple[float, float]]:
        rssi_map = {m.bssid: m.rssi_dbm for m in request.measurements}
        for p in processed:
            if p["bssid"] not in rssi_map:
                rssi_map[p["bssid"]] = p.get("raw_rssi", -70)
        bearings = self._bearing_store.bearings
        return positions_from_signals(
            [{"bssid": b, "raw_rssi": r} for b, r in rssi_map.items()],
            bearings,
            self._bearing_store.map_radius_m,
        )

    def ProcessBatch(self, request, context):
        try:
            processed = self.processor.process_batch(request)
            processed, batch_corr = enrich_signals(processed)
            processed = self.anomaly_detector.detect_anomalies(processed)

            router_positions = self._map_positions(request, processed)

            motion_conf, events = self.motion_detector.detect_motion(processed)
            lstm_conf = self.lstm.predict(processed)
            cnn_conf, _ = self.cnn.analyze(processed, router_positions)
            motion_conf = max(motion_conf, lstm_conf * 0.85, cnn_conf * 0.75)

            spatial_state = self.spatial.infer_spatial(processed, router_positions)
            stab = stability_index(processed)

            result = pb2.ProcessingResult()
            result.timestamp.GetCurrentTime()
            result.motion_confidence = motion_conf
            result.occupancy_confidence = spatial_state['occupancy_confidence']
            result.classification = _classify(motion_conf)
            result.active_ap_count = len(processed)
            result.stability_index = stab
            result.batch_correlation = batch_corr
            result.lstm_motion_confidence = lstm_conf
            result.cnn_activity_score = cnn_conf

            for p in processed:
                sig_msg = result.signals.add()
                sig_msg.bssid = p['bssid']
                sig_msg.ssid = p['ssid']
                sig_msg.raw_rssi = p['raw_rssi']
                sig_msg.smoothed_rssi = p['smoothed_rssi']
                sig_msg.variance = p['variance']
                sig_msg.std_dev = p['std_dev']
                sig_msg.change_rate = p['change_rate']
                sig_msg.dominant_frequency = p['dominant_frequency']
                sig_msg.spectral_energy = p['spectral_energy']
                sig_msg.z_score = p.get('z_score', 0.0)
                sig_msg.is_anomaly = p.get('is_anomaly', False)
                sig_msg.entropy = p.get('entropy', 0.0)
                sig_msg.cross_correlation = p.get('cross_correlation', 0.0)
                sig_msg.fft_magnitudes.extend(p['fft_magnitudes'])
                sig_msg.fft_frequencies.extend(p['fft_frequencies'])

            for e in events:
                evt_msg = result.events.add()
                evt_msg.event_id = "evt_" + str(time.time())
                evt_msg.timestamp.GetCurrentTime()
                evt_msg.event_type = pb2.MOVEMENT
                evt_msg.confidence = e['confidence']
                evt_msg.description = e['description']
                evt_msg.affected_ap_count = len(processed)
                evt_msg.average_variance = float(
                    sum(p['variance'] for p in processed) / max(1, len(processed))
                )

            for z in spatial_state['zones']:
                zone_msg = result.zones.add()
                zone_msg.zone_id = z['zone_id']
                zone_msg.name = z['name']
                zone_msg.x = z['x']
                zone_msg.y = z['y']
                zone_msg.radius = z['radius']
                zone_msg.occupancy_confidence = z['occupancy_confidence']
                zone_msg.motion_confidence = z['motion_confidence']
                zone_msg.color = z['color']

            return result
        except Exception as e:
            logger.error("Error processing batch: %s", e, exc_info=True)
            context.set_code(grpc.StatusCode.INTERNAL)
            context.set_details(str(e))
            raise

    def StreamProcess(self, request_iterator, context):
        for batch in request_iterator:
            yield self.ProcessBatch(batch, context)


def serve():
    try:
        with open('config.yaml', 'r', encoding='utf-8') as f:
            config = yaml.safe_load(f)
    except FileNotFoundError:
        config = {'server_port': 5051, 'sample_rate_hz': 10.0}

    server = grpc.server(futures.ThreadPoolExecutor(max_workers=10))
    pb2_grpc.add_SignalProcessorServiceServicer_to_server(SignalProcessorService(config), server)
    port = config.get('server_port', 5051)
    server.add_insecure_port(f'[::]:{port}')
    server.start()
    logger.info("Python Signal Processor started on port %s", port)
    try:
        server.wait_for_termination()
    except KeyboardInterrupt:
        server.stop(0)


if __name__ == '__main__':
    serve()
