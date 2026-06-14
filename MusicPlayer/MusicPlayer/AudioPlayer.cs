using NAudio.Wave;
using NAudio.Dsp;
using NAudio.Wave.SampleProviders;
using System.IO;
using System;

namespace MusicPlayer
{
    public enum PlaybackState
    {
        Stopped,
        Playing,
        Paused
    }

    public class AudioPlayer : IDisposable
    {
        private WaveOutEvent outputDevice;
        private WaveStream audioFile;
        private float volume = 0.5f;
        private bool isPositionChanging = false;

        // Búfer circular para almacenar muestras de audio mono
        public CircularSampleBuffer SampleBuffer { get; private set; }

        // Propiedades expuestas para la visualización
        public int SampleRate => audioFile?.WaveFormat.SampleRate ?? 0;
        public int LatencyMilliseconds => 100;
        public int FftLength { get; } = 1024;
        public long AudioPosition => audioFile?.Position ?? 0;

        // Mantener este evento para compatibilidad, aunque ya no lo disparemos desde el audio thread
        public event EventHandler<float[]> FftCalculated;
        public event EventHandler<PlaybackState> PlaybackStateChanged;
        public event EventHandler TrackEnded;

        public TimeSpan TotalTime => audioFile?.TotalTime ?? TimeSpan.Zero;
        public TimeSpan CurrentTime => audioFile?.CurrentTime ?? TimeSpan.Zero;
        public bool IsPlaying => outputDevice?.PlaybackState == NAudio.Wave.PlaybackState.Playing;
        public string CurrentFilePath { get; private set; }

        public float Volume
        {
            get => volume;
            set
            {
                volume = Math.Max(0, Math.Min(1, value));
                if (outputDevice != null)
                    outputDevice.Volume = volume;
            }
        }

        public TimeSpan Position
        {
            get => audioFile?.CurrentTime ?? TimeSpan.Zero;
            set
            {
                if (audioFile != null && !isPositionChanging)
                {
                    isPositionChanging = true;
                    try
                    {
                        // Permitir cambio de posición incluso durante reproducción
                        audioFile.CurrentTime = value;
                    }
                    finally
                    {
                        isPositionChanging = false;
                    }
                }
            }
        }

        public AudioPlayer(int fftLength = 1024)
        {
            FftLength = fftLength;
        }

        public void Load(string filePath)
        {
            Dispose(); // Liberar recursos previos

            CurrentFilePath = filePath;

            // Elegir lector adecuado según la extensión (Media Foundation para contenedores como .mp4/.m4a)
            var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
            if (ext == ".mp4" || ext == ".m4a" || ext == ".wma")
            {
                audioFile = new MediaFoundationReader(filePath);
            }
            else
            {
                // AudioFileReader maneja mp3, wav y otros formatos de audio comunes
                audioFile = new AudioFileReader(filePath);
            }

            // Inicializar el búfer circular para el sample rate actual
            int sampleRate = audioFile.WaveFormat.SampleRate;
            SampleBuffer = new CircularSampleBuffer(sampleRate * 2); // 2 segundos de historial

            outputDevice = new WaveOutEvent { DesiredLatency = 100 };
            outputDevice.Volume = volume;

            // Configurar eventos
            outputDevice.PlaybackStopped += OutputDevice_PlaybackStopped;

            // Inicializar con el proveedor de muestras (convertir a ISampleProvider)
            var sampleProv = audioFile.ToSampleProvider();
            outputDevice.Init(new SampleProviderWrapper(sampleProv, this));

            OnPlaybackStateChanged(PlaybackState.Stopped);
        }

        public void Play()
        {
            outputDevice?.Play();
            OnPlaybackStateChanged(PlaybackState.Playing);
        }

        public void Pause()
        {
            outputDevice?.Pause();
            OnPlaybackStateChanged(PlaybackState.Paused);
        }

        public void Stop()
        {
            outputDevice?.Stop();
            if (audioFile != null)
                audioFile.Position = 0;
            // Limpiar el búfer circular al detener
            SampleBuffer?.Reset();
            OnPlaybackStateChanged(PlaybackState.Stopped);
        }

        private void OutputDevice_PlaybackStopped(object sender, StoppedEventArgs e)
        {
            OnPlaybackStateChanged(PlaybackState.Stopped);

            // Si la canción terminó naturalmente (no fue detenida manualmente)
            if (audioFile != null && audioFile.Position >= audioFile.Length - 1000) // Margen de error
            {
                TrackEnded?.Invoke(this, EventArgs.Empty);
            }
        }

        private void OnPlaybackStateChanged(PlaybackState state)
        {
            PlaybackStateChanged?.Invoke(this, state);
        }

        public void Dispose()
        {
            outputDevice?.Stop();
            outputDevice?.Dispose();
            audioFile?.Dispose();
            outputDevice = null;
            audioFile = null;
        }
    }

    public class SampleProviderWrapper : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly AudioPlayer player;

        public SampleProviderWrapper(ISampleProvider source, AudioPlayer player)
        {
            this.source = source;
            this.player = player;
            this.WaveFormat = source.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var samplesRead = source.Read(buffer, offset, count);
            if (samplesRead <= 0) return samplesRead;

            int channels = source.WaveFormat.Channels;
            int monoSamplesCount = samplesRead / channels;

            // Obtener la posición absoluta en bytes después de la lectura
            long bytePosition = player.AudioPosition;
            int bytesPerFrame = source.WaveFormat.BlockAlign;
            long endFrameIndex = bytePosition / bytesPerFrame;
            long startFrameIndex = endFrameIndex - monoSamplesCount;

            // Convertir a mono sumando todos los canales y promediando
            float[] monoSamples = new float[monoSamplesCount];
            for (int i = 0; i < monoSamplesCount; i++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    sum += buffer[offset + i * channels + c];
                }
                monoSamples[i] = sum / channels;
            }

            // Escribir muestras mono en el búfer circular
            player.SampleBuffer?.Write(monoSamples, startFrameIndex, monoSamplesCount);

            return samplesRead;
        }
    }

    public class CircularSampleBuffer
    {
        private readonly float[] buffer;
        private readonly int size;
        private long totalSamplesWritten;
        private readonly object lockObject = new object();

        public CircularSampleBuffer(int size)
        {
            this.size = size;
            this.buffer = new float[size];
            this.totalSamplesWritten = 0;
        }

        public void Reset()
        {
            lock (lockObject)
            {
                Array.Clear(buffer, 0, buffer.Length);
                totalSamplesWritten = 0;
            }
        }

        public void Write(float[] samples, long startPosition, int count)
        {
            lock (lockObject)
            {
                for (int i = 0; i < count; i++)
                {
                    long pos = startPosition + i;
                    int index = (int)(pos % size);
                    buffer[index] = samples[i];
                }
                if (startPosition + count > totalSamplesWritten)
                {
                    totalSamplesWritten = startPosition + count;
                }
            }
        }

        public void Read(float[] output, long startPosition, int count)
        {
            lock (lockObject)
            {
                for (int i = 0; i < count; i++)
                {
                    long pos = startPosition + i;
                    if (pos < 0 || pos >= totalSamplesWritten)
                    {
                        output[i] = 0;
                    }
                    else
                    {
                        // Si la posición solicitada ya fue sobreescrita
                        if (totalSamplesWritten - pos > size)
                        {
                            output[i] = 0;
                        }
                        else
                        {
                            int index = (int)(pos % size);
                            output[i] = buffer[index];
                        }
                    }
                }
            }
        }
    }
}