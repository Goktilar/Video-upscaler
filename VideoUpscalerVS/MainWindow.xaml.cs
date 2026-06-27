using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using System.Globalization;

namespace VideoUpscalerVS
{
    public struct ProgressInfo
    {
        public double Percentage { get; set; }
        public string RemainingTime { get; set; }
    }

    public enum ThemeMode { System, Light, Dark }

    public partial class MainWindow : System.Windows.Window
    {
        private string selectedPath = "";
        private Localization currentLang;
        private CancellationTokenSource? cts;
        private ThemeMode currentThemeMode = ThemeMode.System;
        private string currentLangKey = "English";

        public MainWindow()
        {
            InitializeComponent();
            DetectSystemDefaults();
            UpdateLocalization();
            ApplyTheme();
        }

        private void DetectSystemDefaults()
        {
            // Language detection
            string sysLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (sysLang == "ru")
            {
                currentLangKey = "Русский";
                LangCombo.SelectedIndex = 1;
            }
            else
            {
                currentLangKey = "English";
                LangCombo.SelectedIndex = 0;
            }

            // Theme detection (Default to System)
            currentThemeMode = ThemeMode.System;
        }

        private bool IsSystemInDarkMode()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var val = key?.GetValue("AppsUseLightTheme");
                    if (val != null) return (int)val == 0;
                }
            }
            catch { }
            return false;
        }

        private void LangCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LangCombo == null) return;
            currentLangKey = (LangCombo.SelectedIndex == 1) ? "Русский" : "English";
            UpdateLocalization();
        }

        private void ThemeBtn_Click(object sender, RoutedEventArgs e)
        {
            // Cycle: System -> Light -> Dark -> System
            currentThemeMode = currentThemeMode switch
            {
                ThemeMode.System => ThemeMode.Light,
                ThemeMode.Light => ThemeMode.Dark,
                _ => ThemeMode.System
            };
            ApplyTheme();
        }

        private void UpdateLocalization()
        {
            currentLang = Languages[currentLangKey];

            TitleLabel.Text = currentLang.Title;
            SelectFileBtn.Content = currentLang.UploadVideo;
            StartBtn.Content = currentLang.StartButton;
            CancelBtn.Content = currentLang.CancelButton;
            MethodLabel.Text = currentLang.Interpolation;
            DeviceLabel.Text = currentLang.Device;

            int prevMethodIndex = MethodCombo?.SelectedIndex ?? 0;
            MethodCombo.Items.Clear();
            foreach (var m in currentLang.InterpMethods) MethodCombo.Items.Add(m);
            MethodCombo.SelectedIndex = Math.Max(0, prevMethodIndex);

            int prevDeviceIndex = DeviceCombo?.SelectedIndex ?? 0;
            DeviceCombo.Items.Clear();
            foreach (var d in currentLang.Devices) DeviceCombo.Items.Add(d);
            DeviceCombo.SelectedIndex = Math.Max(0, prevDeviceIndex);

            if (FilePathLabel.Text == "No file selected" || FilePathLabel.Text == "Файл не выбран")
                FilePathLabel.Text = currentLang.NoFile;

            FactorLabel.Text = $"{currentLang.UpscaleFactor} (x{FactorSlider.Value:F1}):";
        }

        private void ApplyTheme()
        {
            bool useDark;
            if (currentThemeMode == ThemeMode.System)
            {
                ThemeBtn.Content = "🌓";
                useDark = IsSystemInDarkMode();
            }
            else if (currentThemeMode == ThemeMode.Dark)
            {
                ThemeBtn.Content = "🌙";
                useDark = true;
            }
            else
            {
                ThemeBtn.Content = "☀️";
                useDark = false;
            }

            var bg = useDark ? new SolidColorBrush(Color.FromRgb(30, 30, 30)) : Brushes.White;
            var panelBg = useDark ? new SolidColorBrush(Color.FromRgb(45, 45, 48)) : new SolidColorBrush(Color.FromRgb(240, 240, 240));
            var fg = useDark ? Brushes.White : Brushes.Black;
            var border = useDark ? new SolidColorBrush(Color.FromRgb(63, 63, 70)) : Brushes.Gray;

            this.Background = bg;
            MainGrid.Background = bg;
            TitleLabel.Foreground = fg;
            FilePathLabel.Foreground = fg;
            FactorLabel.Foreground = fg;
            MethodLabel.Foreground = fg;
            DeviceLabel.Foreground = fg;
            ETALabel.Foreground = fg;
            PercLabel.Foreground = fg;

            // Set App-wide theme if possible or just target controls
            UpdateControlTheme(this, useDark, fg, panelBg, border);
        }

        private void UpdateControlTheme(DependencyObject parent, bool isDark, Brush fg, Brush bg, Brush border)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Control c && !(c is Button && (c.Name == "StartBtn" || c.Name == "CancelBtn")))
                {
                    c.Foreground = fg;
                    if (c is ComboBox || c is TextBox) c.Background = bg;
                }
                UpdateControlTheme(child, isDark, fg, bg, border);
            }
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
            if (FactorLabel != null && currentLang != null)
                FactorLabel.Text = $"{currentLang.UpscaleFactor} (x{e.NewValue:F1}):";
        }

        private async void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(selectedPath)) return;

            string tempOutputPath = Path.Combine(Path.GetTempPath(), "upscale_temp_" + Guid.NewGuid().ToString("N") + ".mp4");
            string finalOutputPath = Path.Combine(Path.GetDirectoryName(selectedPath), Path.GetFileNameWithoutExtension(selectedPath) + "_upscaled.mp4");

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
                await Task.Run(() => UpscaleLogic(selectedPath, tempOutputPath, factor, methodIdx, deviceIdx, progress, cts.Token), cts.Token);
                StatusLabel.Text = "Muxing audio...";
                await Task.Run(() => MuxAudio(selectedPath, tempOutputPath, finalOutputPath, cts.Token), cts.Token);
                StatusLabel.Text = currentLang.Success;
                MessageBox.Show(currentLang.Success + "\nSaved to: " + finalOutputPath);
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex.InnerException is OperationCanceledException)
            {
                StatusLabel.Text = currentLang.Cancelled;
                StatusLabel.Foreground = Brushes.Red;
                if (File.Exists(finalOutputPath)) try { File.Delete(finalOutputPath); } catch { }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error: " + ex.Message);
            }
            finally
            {
                if (File.Exists(tempOutputPath)) try { File.Delete(tempOutputPath); } catch { }
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
            FactorSlider.IsEnabled = enabled;
            MethodCombo.IsEnabled = enabled;
            DeviceCombo.IsEnabled = enabled;
            LangCombo.IsEnabled = enabled;
            ThemeBtn.IsEnabled = enabled;
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
                if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();
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

        private void MuxAudio(string originalVideo, string upscaledVideo, string outputVideo, CancellationToken token)
        {
            string args = $"-i \"{upscaledVideo}\" -i \"{originalVideo}\" -map 0:v -map 1:a? -c:v copy -c:a copy -shortest \"{outputVideo}\" -y";
            ProcessStartInfo psi = new ProcessStartInfo("ffmpeg", args) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
            using (Process? p = Process.Start(psi))
            {
                if (p == null) return;
                using (token.Register(() => { try { p.Kill(); } catch { } })) p.WaitForExit();
                if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();
                if (p.ExitCode != 0 && !File.Exists(outputVideo)) File.Copy(upscaledVideo, outputVideo, true);
            }
        }

        private Dictionary<string, Localization> Languages = new Dictionary<string, Localization>
        {
            ["English"] = new Localization { Title = "Video Upscaler", NoFile = "No file selected", UploadVideo = "Select Video File", UpscaleFactor = "Upscale Factor", Interpolation = "Interpolation:", Device = "Device:", StartButton = "Start Upscaling", CancelButton = "Cancel", Processing = "Processing...", Success = "Done!", Cancelled = "Cancelled", RemainingTime = "Remaining", InterpMethods = new List<string> { "Lanczos", "Bicubic", "Nearest", "AI (EDSR x2)" }, Devices = new List<string> { "CPU", "NVIDIA (CUDA)", "AMD/Intel (OpenCL)" } },
            ["Русский"] = new Localization { Title = "Видео Апскейлер", NoFile = "Файл не выбран", UploadVideo = "Выбрать видео", UpscaleFactor = "Коэффициент", Interpolation = "Метод:", Device = "Устройство:", StartButton = "Начать", CancelButton = "Отмена", Processing = "Обработка...", Success = "Готово!", Cancelled = "Отменено", RemainingTime = "Осталось", InterpMethods = new List<string> { "Lanczos", "Бикубическая", "Сосед", "ИИ (EDSR x2)" }, Devices = new List<string> { "ЦПУ (CPU)", "NVIDIA (CUDA)", "AMD/Intel (OpenCL)" } }
        };
    }

    public class Localization { public string Title { get; set; } public string NoFile { get; set; } public string UploadVideo { get; set; } public string UpscaleFactor { get; set; } public string Interpolation { get; set; } public string Device { get; set; } public string StartButton { get; set; } public string CancelButton { get; set; } public string Processing { get; set; } public string Success { get; set; } public string Cancelled { get; set; } public string RemainingTime { get; set; } public List<string> InterpMethods { get; set; } public List<string> Devices { get; set; } }
}
