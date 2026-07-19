import numpy as np
from scipy.signal import butter, filtfilt
from scipy.fft import fft, fftfreq
import collections
import logging

class SignalProcessor:
    def __init__(self, window_size=10, cutoff_frequency=2.0, sample_rate=2.0):
        self.window_size = window_size
        self.sample_rate = sample_rate
        self.nyquist = 0.5 * sample_rate
        self.cutoff = cutoff_frequency / self.nyquist
        if self.cutoff >= 1.0:
            self.cutoff = 0.99
        self.b, self.a = butter(4, self.cutoff, btype='low', analog=False)
        self.history = collections.defaultdict(lambda: collections.deque(maxlen=self.window_size))
        self.logger = logging.getLogger(__name__)

    def process_batch(self, batch):
        processed_signals = []
        for m in batch.measurements:
            hist = self.history[m.bssid]
            hist.append(m.rssi_dbm)

            if len(hist) < 4:
                continue

            arr = np.array(hist)
            mean_val = np.mean(arr)
            variance = np.var(arr)
            std_dev = np.std(arr)
            
            # Filter
            try:
                smoothed = filtfilt(self.b, self.a, arr)[-1] if len(arr) > 3 else arr[-1]
            except Exception:
                smoothed = mean_val
                
            # Rate of change
            change_rate = 0.0
            if len(arr) >= 2:
                change_rate = (arr[-1] - arr[0]) / (len(arr) / self.sample_rate)
                
            # FFT — fftfreq already orders non-negative bins ascending first,
            # so the positive-frequency slice is sorted without an extra argsort.
            N = len(arr)
            yf = np.abs(fft(arr - mean_val))
            xf = fftfreq(N, 1/self.sample_rate)

            positive_freqs = xf > 0
            if np.any(positive_freqs):
                dom_freq = xf[positive_freqs][np.argmax(yf[positive_freqs])]
                spectral_energy = np.sum(yf[(xf > 0.5) & (xf < 3.0)]**2)
            else:
                dom_freq = 0.0
                spectral_energy = 0.0

            processed_signals.append({
                'bssid': m.bssid,
                'ssid': m.ssid,
                'raw_rssi': m.rssi_dbm,
                'smoothed_rssi': float(smoothed),
                'variance': float(variance),
                'std_dev': float(std_dev),
                'change_rate': float(change_rate),
                'dominant_frequency': float(dom_freq),
                'spectral_energy': float(spectral_energy),
                'fft_magnitudes': yf[positive_freqs].tolist(),
                'fft_frequencies': xf[positive_freqs].tolist(),
                '_history': arr,
            })
            
        return processed_signals
