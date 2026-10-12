using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp.WpfExtensions;

namespace Project_FREAK.Controllers
{
    public partial class WebcamManager : IDisposable
    {
        private VideoCapture? _capture; // Manages video capture from a camera or video file
        private CancellationTokenSource? _cts; // Token source to cancel the capture loop
        public event Action<BitmapSource>? FrameReceived; // Event triggered when a new frame is received
        private readonly Dispatcher _dispatcher; // Dispatcher to ensure UI updates occur on the main thread
        public event Action<string>? CameraError; // Event triggered when a camera error occurs

        private VideoWriter? _videoWriter;
        private BlockingCollection<Mat>? _recordingQueue;
        private Task? _recordingTask;
        private readonly object _recordingLock = new();
        private int _isRenderingFrame = 0;

        public int FrameWidth { get; private set; }
        public int FrameHeight { get; private set; }
        public double Fps { get; private set; }

        public WebcamManager(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        // Initializes the webcam manager asynchronously
        public async Task InitializeAsync(bool demoMode, string? rtspUrl = null)
        {
            try
            {
                await Task.Run(() =>
                {
                    if (demoMode)
                    {
                        // Use DirectShow on Windows for lowest capture latency
                        _capture = new VideoCapture(0, VideoCaptureAPIs.DSHOW);
                        if (!_capture.IsOpened())
                        {
                            _capture = new VideoCapture(0);
                        }
                    }
                    else if (!string.IsNullOrEmpty(rtspUrl))
                    {
                        // Configure low-latency RTSP flags for FFmpeg
                        Environment.SetEnvironmentVariable("OPENCV_FFMPEG_CAPTURE_OPTIONS", "rtsp_transport;tcp|fflags;nobuffer|flags;low_delay|max_delay;500000");
                        _capture = new VideoCapture(rtspUrl, VideoCaptureAPIs.FFMPEG);
                        if (!_capture.IsOpened())
                        {
                            _capture = new VideoCapture(rtspUrl);
                        }
                    }
                });

                if (_capture?.IsOpened() == true)
                {
                    try
                    {
                        // Request minimum driver frame buffer to prevent latency accumulation
                        _capture.Set(VideoCaptureProperties.BufferSize, 1);
                    }
                    catch { }

                    FrameWidth = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
                    FrameHeight = (int)_capture.Get(VideoCaptureProperties.FrameHeight);
                    Fps = _capture.Get(VideoCaptureProperties.Fps);
                    if (Fps <= 0 || Fps > 60)
                    {
                        // Default to 30 FPS if invalid or unrealistic value
                        Fps = 30;
                    }
                    StartCaptureLoop();
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Camera initialization failed", ex);
            }
        }

        // Reinitializes the webcam manager asynchronously
        public async Task ReinitializeAsync(bool demoMode, string? rtspUrl = null)
        {
            Dispose(); // Dispose the current capture
            await InitializeAsync(demoMode, rtspUrl); // Reinitialize with new parameters
        }

        // Starts the capture loop to continuously capture frames
        private void StartCaptureLoop()
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            Task.Run(() =>
            {
                while (!token.IsCancellationRequested && _capture?.IsOpened() == true)
                {
                    try
                    {
                        using var frame = new Mat();
                        if (!_capture.Read(frame) || frame.Empty())
                        {
                            Thread.Sleep(10);
                            continue;
                        }

                        // Asynchronously record video without blocking live capture
                        var queue = _recordingQueue;
                        if (queue != null && !queue.IsAddingCompleted)
                        {
                            var frameClone = frame.Clone();
                            if (!queue.TryAdd(frameClone))
                            {
                                // Drop frame if queue is backlogged to protect system memory
                                frameClone.Dispose();
                            }
                        }

                        // Non-blocking UI preview gate: drop preview frame if UI is still rendering
                        if (Interlocked.CompareExchange(ref _isRenderingFrame, 1, 0) == 0)
                        {
                            try
                            {
                                var bitmap = frame.ToWriteableBitmap();
                                bitmap.Freeze();

                                _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                                {
                                    try
                                    {
                                        FrameReceived?.Invoke(bitmap);
                                    }
                                    finally
                                    {
                                        Interlocked.Exchange(ref _isRenderingFrame, 0);
                                    }
                                }));
                            }
                            catch
                            {
                                Interlocked.Exchange(ref _isRenderingFrame, 0);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        CameraError?.Invoke($"Frame error: {ex.Message}");
                    }
                }
            }, token);
        }

        // Starts recording video to the specified file path
        public void StartRecording(string filePath)
        {
            lock (_recordingLock)
            {
                if (_capture == null || !_capture.IsOpened())
                    throw new InvalidOperationException("Camera not initialized.");

                StopRecordingInternal();

                int fourcc = VideoWriter.FourCC('a', 'v', 'c', '1');    // Default to AVC codec
                if (!IsFourccSupported(fourcc))
                {
                    fourcc = VideoWriter.FourCC('m', 'p', '4', 'v');    // Fallback to MP4V codec
                }

                _videoWriter = new VideoWriter(     // Initialize the video writer
                    filePath,
                    fourcc,
                    Fps,
                    new OpenCvSharp.Size(FrameWidth, FrameHeight)
                );

                if (!_videoWriter.IsOpened())
                    throw new Exception("Failed to initialize video writer.");

                _recordingQueue = new BlockingCollection<Mat>(boundedCapacity: 120);
                var activeQueue = _recordingQueue;
                var activeWriter = _videoWriter;

                // Dedicated background worker for video encoding/file writing
                _recordingTask = Task.Run(() =>
                {
                    try
                    {
                        foreach (var frame in activeQueue.GetConsumingEnumerable())
                        {
                            try
                            {
                                activeWriter.Write(frame);
                            }
                            finally
                            {
                                frame.Dispose();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        CameraError?.Invoke($"Recording error: {ex.Message}");
                    }
                });
            }
        }

        // Checks if the specified FourCC codec is supported
        private static bool IsFourccSupported(int fourcc)
        {
            const string testFile = "test_codec.mp4";
            try
            {
                using (var testWriter = new VideoWriter(testFile, fourcc, 30, new OpenCvSharp.Size(640, 480)))
                {
                    return testWriter.IsOpened();
                }
            }
            catch { return false; }
            finally
            {
                try { if (File.Exists(testFile)) File.Delete(testFile); } catch { }
            }
        }

        // Stops the video recording
        public void StopRecording()
        {
            lock (_recordingLock)
            {
                StopRecordingInternal();
            }
        }

        private void StopRecordingInternal()
        {
            if (_recordingQueue != null)
            {
                var recordingQueue = _recordingQueue;
                recordingQueue.CompleteAdding();
                try
                {
                    _recordingTask?.GetAwaiter().GetResult();
                }
                catch { }
                recordingQueue.Dispose();
                _recordingQueue = null;
                _recordingTask = null;
            }

            _videoWriter?.Release();
            _videoWriter?.Dispose();
            _videoWriter = null;
        }

        // Disposes the webcam manager and releases resources
        public void Dispose()
        {
            _cts?.Cancel();
            StopRecording();
            _capture?.Dispose();
            _capture = null;
            GC.SuppressFinalize(this);
        }
    }
}
