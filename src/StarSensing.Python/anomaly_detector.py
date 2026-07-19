import numpy as np

class AnomalyDetector:
    def __init__(self):
        self.stats = {}

    def detect_anomalies(self, processed_signals):
        results = []
        for s in processed_signals:
            bssid = s['bssid']
            val = s['raw_rssi']
            
            if bssid not in self.stats:
                self.stats[bssid] = {'mean': val, 'std': 1.0, 'count': 1}
                s['z_score'] = 0.0
                s['is_anomaly'] = False
                continue
                
            stat = self.stats[bssid]
            z = (val - stat['mean']) / (stat['std'] + 1e-6)
            
            # Update stats (exponential moving average)
            alpha = 0.1
            stat['mean'] = (1 - alpha) * stat['mean'] + alpha * val
            stat['std'] = np.sqrt((1 - alpha) * stat['std']**2 + alpha * (val - stat['mean'])**2)
            
            s['z_score'] = float(z)
            s['is_anomaly'] = bool(abs(z) > 3.0)
            
        return processed_signals
