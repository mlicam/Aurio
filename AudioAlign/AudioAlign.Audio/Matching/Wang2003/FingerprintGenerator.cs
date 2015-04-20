﻿using AudioAlign.Audio.DataStructures;
using AudioAlign.Audio.Project;
using AudioAlign.Audio.Streams;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace AudioAlign.Audio.Matching.Wang2003 {
    public class FingerprintGenerator {

        private int samplingRate = 11025;
        private int windowSize = 512;
        private int hopSize = 256;

        private int spectrumSmoothingLength = 3; // the width in samples of the FFT spectrum to average over
        private int peaksPerFrame = 3;
        private int peakFanout = 5;

        private int targetZoneDistance = 2; // time distance in frames
        private int targetZoneLength = 30; // time length in frames
        private int targetZoneWidth = 63; // frequency width in FFT bins

        public event EventHandler<FrameProcessedEventArgs> FrameProcessed;

        public FingerprintGenerator() {
            //
        }

        public void Generate(AudioTrack track) {
            IAudioStream audioStream = new ResamplingStream(
                new MonoStream(AudioStreamFactory.FromFileInfoIeee32(track.FileInfo)),
                ResamplingQuality.Medium, samplingRate);

            STFT stft = new STFT(audioStream, windowSize, hopSize, WindowType.Hann);
            int index = 0;
            int indices = stft.WindowCount;

            float[] spectrum = new float[windowSize / 2];
            //float[] smoothedSpectrum = new float[frameBuffer.Length - frameSmoothingLength + 1]; // the smooved frequency spectrum of the current frame
            //var spectrumSmoother = new SimpleMovingAverage(frameSmoothingLength);
            float[] spectrumTemporalAverage = new float[spectrum.Length]; // a running average of each spectrum bin over time
            float[] spectrumResidual = new float[spectrum.Length]; // the difference between the current spectrum and the moving average spectrum

            var peaks = new List<Peak>(spectrum.Length / 2); // keep a single instance of the list to avoid instantiation overhead

            while (stft.HasNext()) {
                // Get the FFT spectrum
                stft.ReadFrame(spectrum);

                // Smooth the frequency spectrum to remove small peaks
                //spectrumSmoother.Clear();
                //for (int i = 0; i < frameBuffer.Length; i++) {
                //    var avg = spectrumSmoother.Add(frameBuffer[i]);
                //    if (i >= spectrumSmoothingLength) {
                //        smoothedSpectrum[i - spectrumSmoothingLength] = avg;
                //    }
                //}

                // Update the temporal moving bin average
                if (index == 0) {
                    // Init averages on first frame
                    for (int i = 0; i < spectrum.Length; i++) {
                        spectrumTemporalAverage[i] = spectrum[i];
                    }
                }
                else {
                    // Update averages on all subsequent frames
                    for (int i = 0; i < spectrum.Length; i++) {
                        spectrumTemporalAverage[i] = ExponentialMovingAverage.UpdateMovingAverage(spectrumTemporalAverage[i], 0.05f, spectrum[i]);
                    }
                }

                // Calculate the residual
                // The residual is the difference of the current spectrum to the temporal average spectrum. The higher
                // a bin residual is, the steeper the increase in energy in that peak.
                for (int i = 0; i < spectrum.Length; i++) {
                    spectrumResidual[i] = spectrum[i] - spectrumTemporalAverage[i] - 90f;
                }

                // Find local peaks in the residual
                // The advantage of finding peaks in the residual instead of the spectrum is that spectrum energy is usually
                // concentrated in the low frequencies, resulting in a clustering if the highest peaks in the lows. Getting
                // peaks from the residual distributes the peaks more evenly across the spectrum.
                peaks.Clear();
                FindLocalMaxima(spectrumResidual, peaks);

                // Pick the largest n peaks
                int numMaxima = Math.Min(peaks.Count, peaksPerFrame);
                if (numMaxima > 0) {
                    peaks.Sort((p1, p2) => p1.Value == p2.Value ? 0 : p1.Value < p2.Value ? 1 : -1); // order peaks by height
                    if (peaks.Count > numMaxima) {
                        peaks.RemoveRange(numMaxima, peaks.Count - numMaxima); // select the n tallest peaks by deleting the rest
                    }
                    peaks.Sort((p1, p2) => p1.Index == p2.Index ? 0 : p1.Index < p2.Index ? -1 : 1); // sort peaks by index (not really necessary)
                }

                if (peaks != null) {
                    // Mark peaks as 0dB for spectrogram display purposes
                    foreach (var peak in peaks) {
                        spectrum[peak.Index] = 0;
                        spectrumResidual[peak.Index] = 0;
                    }
                }
                
                if (FrameProcessed != null) {
                    FrameProcessed(this, new FrameProcessedEventArgs { 
                        AudioTrack = track, Index = index, Indices = indices,
                        Spectrum = spectrum, SpectrumResidual = spectrumResidual
                    });
                }

                index++;
            }
        }

        /// <summary>
        /// Local peak picking works as follows: 
        /// A local peak is always a highest value surrounded by lower values. 
        /// In case of a plateu, the index if the first plateu value marks the peak.
        /// 
        ///      |      |    |
        ///      |      |    |
        ///      v      |    |
        ///      ___    |    |
        ///     /   \   v    v
        ///   _/     \       /\_
        ///  /        \_/\  /   \
        /// /             \/     \
        /// </summary>
        private void FindLocalMaxima(float[] data, List<Peak> peakList) {
            float val;
            float lastVal = float.MinValue;
            int anchorIndex = -1;
            for (int i = 0; i < data.Length; i++) {
                val = data[i];

                if (val > lastVal) {
                    // Climbing an increasing slope to a local maximum
                    anchorIndex = i;
                }
                else if (val == lastVal) {
                    // Plateau
                    // anchorIndex stays the same, as the maximum is always at the beginning of a plateau
                }
                else {
                    // Value is decreasing, going down the decreasing slope
                    if (anchorIndex > -1) {
                        // Local maximum found
                        // The first decrease always comes after a peak (or plateau), 
                        // so the last set anchorIndex is the index of the peak.
                        peakList.Add(new Peak(anchorIndex, lastVal));
                        anchorIndex = -1;
                    }
                }

                lastVal = val;
            }

            //Debug.WriteLine("{0} local maxima found", maxima.Count);
        }

        private struct Peak {

            private int index;
            private float value;

            public Peak(int index, float value) {
                this.index = index;
                this.value = value;
            }
            public int Index { get { return index; } }
            public float Value { get { return value; } }
        }
    }
}
