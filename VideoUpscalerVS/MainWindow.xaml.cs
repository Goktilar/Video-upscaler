using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace VideoUpscalerVS
{
    public struct ProgressInfo
    {
        public double Percentage { get; set; }
        public string RemainingTime { get; set; }
    }

    public partial class MainWindow : System.Windows.Window
    {
        private string selectedPath = "";
        private Localization currentLang;
        private CancellationTokenSource? cts;

        public MainWindow()
        {
            InitializeComponent();
            LangCombo.SelectedIndex = 0; // English
            ThemeCombo.SelectedIndex = 0; // Light
        }

        private void LangCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateLocalization();
        }

        private void UpdateLocalization()
        {
            if (LangCombo == null) return;
            string lang = (LangCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content.ToString() ?? "English";
            currentLang = Languages[lang];

            TitleLabel.Text = currentLang.Title;
            LangLabel.Text = currentLang.SelectLang;
            ThemeLabel.Text = currentLang.SelectTheme;
            SelectFileBtn.Content = currentLang.UploadVideo;
            StartBtn.Content = currentLang.StartButton;
            CancelBtn.Content = currentLang.CancelButton;
            MethodLabel.Text = currentLang.Interpolation;
            DeviceLabel.Text = currentLang.Device;

            int prevMethodIndex = MethodCombo?.SelectedIndex ?? 0;
            MethodCombo.Items.Clear();
            foreach (var m in currentLang.InterpMethods) MethodCombo.Items.Add(m);
            MethodCombo.SelectedIndex = prevMethodIndex >= 0 ? prevMethodIndex : 0;

            int prevDeviceIndex = DeviceCombo?.SelectedIndex ?? 0;
            DeviceCombo.Items.Clear();
            foreach (var d in currentLang.Devices) DeviceCombo.Items.Add(d);
            DeviceCombo.SelectedIndex = prevDeviceIndex >= 0 ? prevDeviceIndex : 0;
        }

        private void ThemeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ThemeCombo == null) return;
            string theme = (ThemeCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content.ToString() ?? "Light";

            bool isDark = theme == "Dark" || theme == "Темная";
            MainGrid.Background = isDark ? new SolidColorBrush(Color.FromRgb(14, 17, 23)) : System.Windows.Media.Brushes.White;
            var brush = isDark ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black;

            TitleLabel.Foreground = brush;
            LangLabel.Foreground = brush;
            ThemeLabel.Foreground = brush;
            FilePathLabel.Foreground = brush;
            FactorLabel.Foreground = brush;
            MethodLabel.Foreground = brush;
            DeviceLabel.Foreground = brush;
            ETALabel.Foreground = brush;
        }

        private void SelectFileBtn_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "Video files (*.mp4;*.avi;*.mov)|*.mp4;*.avi;*.mov|All files (*.*)|*.*";
            if (openFileDialog.ShowDialog() == true)
            {
                selectedPath = openFileDialog.FileName;
                FilePathLabel.Text = selectedPath;
            }
        }

        private void FactorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (FactorLabel != null)
                FactorLabel.Text = $"{(currentLang != null ? currentLang.UpscaleFactor : "Factor")} (x{e.NewValue:F1}):";
        }

        private async void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(selectedPath)) return;

            string tempOutputPath = Path.Combine(Path.GetDirectoryName(selectedPath),
                Path.GetFileNameWithoutExtension(selectedPath) + "_temp_no_audio.mp4");
            string finalOutputPath = Path.Combine(Path.GetDirectoryName(selectedPath),
                Path.GetFileNameWithoutExtension(selectedPath) + "_upscaled.mp4");

            double factor = FactorSlider.Value;
            int methodIdx = MethodCombo.SelectedIndex;
            int deviceIdx = DeviceCombo.SelectedIndex;

            SetUIEnabled(false);
            ProgressGrid.Visibility = Visibility.Visible;
            StatusLabel.Text = currentLang.Processing;
            StatusLabel.Foreground = Brushes.Green;
            ProgBar.Value = 0;
            PercLabel.Text = "0%";
            ETALabel.Text = "";

            cts = new CancellationTokenSource();
            var progress = new Progress<ProgressInfo>(info =>
            {
                ProgBar.Value = info.Percentage;
                PercLabel.Text = $"{(int)info.Percentage}%";
                ETALabel.Text = $"{currentLang.RemainingTime}: {info.RemainingTime}";
            });

            try
            {
                // 1. Upscale video (no audio)
                await Task.Run(() => UpscaleLogic(selectedPath, tempOutputPath, factor, methodIdx, deviceIdx, progress, cts.Token));

                // 2. Mux audio from original
                StatusLabel.Text = "Muxing audio...";
                await Task.Run(() => MuxAudio(selectedPath, tempOutputPath, finalOutputPath));

                StatusLabel.Text = currentLang.Success;
                System.Windows.MessageBox.Show(currentLang.Success + "\nSaved to: " + finalOutputPath);
            }
            catch (OperationCanceledException)
            {
                StatusLabel.Text = currentLang.Cancelled;
                StatusLabel.Foreground = Brushes.Red;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Error: " + ex.Message);
            }
            finally
            {
                if (File.Exists(tempOutputPath)) try { File.Delete(tempOutputPath); } catch { }
                if (cts?.IsCancellationRequested == true && File.Exists(finalOutputPath)) try { File.Delete(finalOutputPath); } catch { }

                ProgressGrid.Visibility = Visibility.Collapsed;
                SetUIEnabled(true);
                cts?.Dispose();
                cts = null;
            }
        }

        private void SetUIEnabled(bool enabled)
        {
            StartBtn.IsEnabled = enabled;
            SelectFileBtn.IsEnabled = enabled;
            LangCombo.IsEnabled = enabled;
            ThemeCombo.IsEnabled = enabled;
            FactorSlider.IsEnabled = enabled;
            MethodCombo.IsEnabled = enabled;
            DeviceCombo.IsEnabled = enabled;
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            cts?.Cancel();
        }

        private void UpscaleLogic(string input, string output, double factor, int methodIdx, int deviceIdx, IProgress<ProgressInfo> progress, CancellationToken token)
        {
            using var capture = new VideoCapture(input);
            int width = capture.FrameWidth;
            int height = capture.FrameHeight;
            double fps = capture.Fps;
            int totalFrames = capture.FrameCount;
            int fourcc = VideoWriter.FourCC('m', 'p', '4', 'v');

            int newWidth = (int)(width * factor);
            int newHeight = (int)(height * factor);

            using var writer = new VideoWriter(output, fourcc, fps, new OpenCvSharp.Size(newWidth, newHeight));
            using var frame = new Mat();
            using var upscaled = new Mat();

            Net? net = null;
            if (methodIdx == 3)
            {
                string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models/EDSR_x2.pb");
                if (File.Exists(modelPath))
                {
                    net = CvDnn.ReadNetFromTensorflow(modelPath);
                    if (deviceIdx == 1) { net.SetPreferableBackend(Backend.CUDA); net.SetPreferableTarget(Target.CUDA); }
                    else if (deviceIdx == 2) { net.SetPreferableTarget(Target.OPENCL); }
                }
                else methodIdx = 0;
            }

            Stopwatch sw = Stopwatch.StartNew();
            int currentFrame = 0;

            while (capture.Read(frame))
            {
                token.ThrowIfCancellationRequested();
                if (frame.Empty()) break;

                if (methodIdx == 3 && net != null)
                {
                    using var blob = CvDnn.BlobFromImage(frame, 1.0, new OpenCvSharp.Size(frame.Width, frame.Height), new Scalar(), false, false);
                    net.SetInput(blob);
                    using var resultBlob = net.Forward();
                    int outH = resultBlob.Size(2);
                    int outW = resultBlob.Size(3);
                    using var plane0 = Mat.FromPixelData(outH, outW, MatType.CV_32FC1, resultBlob.Ptr(0, 0));
                    using var plane1 = Mat.FromPixelData(outH, outW, MatType.CV_32FC1, resultBlob.Ptr(0, 1));
                    using var plane2 = Mat.FromPixelData(outH, outW, MatType.CV_32FC1, resultBlob.Ptr(0, 2));
                    using var merged = new Mat();
                    Cv2.Merge(new[] { plane0, plane1, plane2 }, merged);
                    merged.ConvertTo(upscaled, MatType.CV_8UC3);
                    if (factor != 2.0) Cv2.Resize(upscaled, upscaled, new OpenCvSharp.Size(newWidth, newHeight), 0, 0, InterpolationFlags.Lanczos4);
                }
                else
                {
                    InterpolationFlags flag = methodIdx switch { 0 => InterpolationFlags.Lanczos4, 1 => InterpolationFlags.Cubic, 2 => InterpolationFlags.Nearest, _ => InterpolationFlags.Lanczos4 };
                    Cv2.Resize(frame, upscaled, new OpenCvSharp.Size(newWidth, newHeight), 0, 0, flag);
                }
                writer.Write(upscaled);

                currentFrame++;
                if (totalFrames > 0 && currentFrame % 5 == 0)
                {
                    double perc = (double)currentFrame / totalFrames * 100.0;
                    double msPerFrame = sw.ElapsedMilliseconds / (double)currentFrame;
                    double remainingMs = msPerFrame * (totalFrames - currentFrame);
                    TimeSpan t = TimeSpan.FromMilliseconds(remainingMs);
                    string eta = t.TotalHours >= 1 ? $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes:D2}:{t.Seconds:D2}";
                    progress.Report(new ProgressInfo { Percentage = perc, RemainingTime = eta });
                }
            }
            net?.Dispose();
        }

        private void MuxAudio(string originalVideo, string upscaledVideo, string outputVideo)
        {
            // Use ffmpeg to copy audio from original to upscaled.
            // Requirement: ffmpeg must be in PATH or in app directory.
            string args = $"-i \"{upscaledVideo}\" -i \"{originalVideo}\" -map 0:v -map 1:a? -c:v copy -c:a copy -shortest \"{outputVideo}\" -y";

            ProcessStartInfo psi = new ProcessStartInfo("ffmpeg", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using (Process? p = Process.Start(psi))
            {
                p?.WaitForExit();
                if (p?.ExitCode != 0 && !File.Exists(outputVideo))
                {
                    // If muxing fails (e.g. no ffmpeg), just rename the upscaled video to final (without audio)
                    File.Copy(upscaledVideo, outputVideo, true);
                }
            }
        }

        private Dictionary<string, Localization> Languages = new Dictionary<string, Localization>
        {
            ["English"] = new Localization { Title = "Video Upscaler", SelectLang = "Language:", SelectTheme = "Theme:", UploadVideo = "Select Video File", UpscaleFactor = "Upscale Factor", Interpolation = "Interpolation:", Device = "Device (Optimization):", StartButton = "Start Upscaling", CancelButton = "Cancel", Processing = "Processing...", Success = "Done!", Cancelled = "Cancelled", RemainingTime = "Remaining time", InterpMethods = new List<string> { "Lanczos", "Bicubic", "Nearest", "AI (EDSR x2)" }, Devices = new List<string> { "CPU", "NVIDIA (CUDA)", "AMD/Intel (OpenCL)" } },
            ["Русский"] = new Localization { Title = "Видео Апскейлер", SelectLang = "Язык:", SelectTheme = "Тема:", UploadVideo = "Выбрать видео", UpscaleFactor = "Коэффициент", Interpolation = "Метод:", Device = "Устройство:", StartButton = "Начать", CancelButton = "Отмена", Processing = "Обработка...", Success = "Готово!", Cancelled = "Отменено", RemainingTime = "Осталось времени", InterpMethods = new List<string> { "Lanczos", "Бикубическая", "Сосед", "ИИ (EDSR x2)" }, Devices = new List<string> { "ЦПУ (CPU)", "NVIDIA (CUDA)", "AMD/Intel (OpenCL)" } }
        };
    }

    public class Localization
    {
        public string Title { get; set; } public string SelectLang { get; set; } public string SelectTheme { get; set; } public string UploadVideo { get; set; } public string UpscaleFactor { get; set; } public string Interpolation { get; set; } public string Device { get; set; } public string StartButton { get; set; } public string CancelButton { get; set; } public string Processing { get; set; } public string Success { get; set; } public string Cancelled { get; set; } public string RemainingTime { get; set; } public List<string> InterpMethods { get; set; } public List<string> Devices { get; set; }
    }
}
