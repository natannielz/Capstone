'use client';

import { useEffect, useRef, useState } from 'react';
import { CAMERA_FORMATS } from './camera-scan';
import common from '../administration/access.module.css';

type Detector = { detect(source: HTMLVideoElement): Promise<Array<{ rawValue: string; format: string }>> };
type DetectorConstructor = new (options: { formats: readonly string[] }) => Detector;

/**
 * Rear-camera barcode reader using the browser's native BarcodeDetector (offered only where it exists).
 * It reports the first read and stops the camera; identity is still resolved by the server like a typed code.
 */
export function CameraScanner({ onDetected, onClose }: { onDetected: (raw: string, format: string) => void; onClose: () => void }) {
  const video = useRef<HTMLVideoElement>(null);
  // Latest callback without restarting the camera when the parent re-renders.
  const detected = useRef(onDetected);
  useEffect(() => { detected.current = onDetected; }, [onDetected]);
  const [message, setMessage] = useState('Arahkan kamera ke barcode kemasan.');
  useEffect(() => {
    let stream: MediaStream | null = null, stopped = false, timer = 0;
    const stop = () => { stopped = true; window.clearTimeout(timer); stream?.getTracks().forEach(track => track.stop()); };
    void (async () => {
      try {
        stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: { ideal: 'environment' } }, audio: false });
        if (stopped || !video.current) { stop(); return; }
        video.current.srcObject = stream; await video.current.play();
        const detector = new (window as unknown as { BarcodeDetector: DetectorConstructor }).BarcodeDetector({ formats: CAMERA_FORMATS });
        const tick = async () => {
          if (stopped || !video.current) return;
          try {
            const [found] = await detector.detect(video.current);
            if (found && !stopped) { stop(); detected.current(found.rawValue, found.format); return; }
          } catch { /* a frame that cannot be read yet */ }
          timer = window.setTimeout(() => void tick(), 250);
        };
        void tick();
      } catch (failure) {
        stop(); // a failed start (play, detector) must not leave the camera running
        setMessage(failure instanceof DOMException && failure.name === 'NotAllowedError'
          ? 'Izin kamera ditolak. Gunakan pemindai atau ketik kode secara manual.' : 'Kamera belum dapat dibuka. Gunakan pemindai atau ketik kode secara manual.');
      }
    })();
    return stop;
  }, []);
  return <div className="camera-scanner" role="group" aria-label="Pindai dengan kamera">
    <video ref={video} muted playsInline aria-label="Pratinjau kamera" />
    <p role="status">{message}</p>
    <button type="button" className={common.secondary} onClick={onClose}>Tutup kamera</button>
  </div>;
}
