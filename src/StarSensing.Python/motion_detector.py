import numpy as np
from sklearn.ensemble import IsolationForest, RandomForestClassifier
import logging
import collections

class MotionDetector:
    def __init__(self):
        self.logger = logging.getLogger(__name__)
        self.iso_forest = IsolationForest(contamination=0.1, random_state=42)
        self.rf = RandomForestClassifier(n_estimators=50, random_state=42)
        self.is_trained = False
        self.baseline_features = []
        
    def extract_features(self, signals):
        if not signals:
            return None
            
        variances = [s['variance'] for s in signals]
        entropies = [s.get('entropy', 0.0) for s in signals]
        energies = [s['spectral_energy'] for s in signals]
        correlations = [s.get('cross_correlation', 0.0) for s in signals]
        
        return np.array([
            np.mean(variances),
            np.max(variances),
            np.mean(entropies),
            np.max(entropies),
            np.mean(energies),
            np.max(energies),
            np.mean(correlations) if correlations else 0.0,
            len(signals)
        ])

    def detect_motion(self, processed_signals):
        features = self.extract_features(processed_signals)
        if features is None:
            return 0.0, []

        self.baseline_features.append(features)
        if len(self.baseline_features) > 100:
            self.baseline_features.pop(0)
            
        if len(self.baseline_features) > 20 and not self.is_trained:
            # Simple dummy training for prototype
            X = np.vstack(self.baseline_features)
            self.iso_forest.fit(X)
            self.is_trained = True
            
        confidence = 0.0
        events = []
        
        if self.is_trained:
            anomaly_score = self.iso_forest.score_samples(features.reshape(1, -1))[0]
            if anomaly_score < -0.5:
                confidence = min(1.0, abs(anomaly_score))
                
        # Basic heuristic fallback
        max_var = np.max([s['variance'] for s in processed_signals]) if processed_signals else 0
        if max_var > 6.0:
            confidence = max(confidence, 1.0)
        elif max_var > 3.0:
            confidence = max(confidence, 0.7)
        elif max_var > 1.5:
            confidence = max(confidence, 0.3)
            
        if confidence > 0.5:
            events.append({
                'type': 'MOVEMENT',
                'confidence': float(confidence),
                'description': 'Motion detected from RF variance'
            })
            
        return float(confidence), events
