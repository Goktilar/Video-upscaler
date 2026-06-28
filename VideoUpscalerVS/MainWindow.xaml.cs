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
        private CancellationTokenSource? cts;
        private ThemeMode currentThemeMode = ThemeMode.System;
        private string currentLangKey = "English";

        public MainWindow()
        {
            InitializeComponent();
            DetectSystemDefaults();

            SystemEvents.UserPreferenceChanged += (s, e) => {
                if (currentThemeMode == ThemeMode.System) ApplyTheme();
            };
        }

        private void DetectSystemDefaults()
        {
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

            SwitchLanguage(currentLangKey == "Русский" ? "ru-RU" : "en-US");
            ApplyTheme();
        }

        private void SwitchLanguage(string cultureCode)
        {
            var dict = new ResourceDictionary { Source = new Uri($"Resources/Languages/{cultureCode}.xaml", UriKind.Relative) };
            for (int i = 0; i < Resources.MergedDictionaries.Count; i++)
            {
                if (Resources.MergedDictionaries[i].Source.OriginalString.Contains("Languages/"))
                {
                    Resources.MergedDictionaries.RemoveAt(i);
                    break;
                }
            }
            Resources.MergedDictionaries.Add(dict);
            UpdateInterpolationAndDevices();
            UpdateLabels();
        }

        private void UpdateLabels()
        {
            if (string.IsNullOrEmpty(selectedPath))
                FilePathLabel.Text = (string)FindResource("NoFile");
            else
                FilePathLabel.Text = selectedPath;

            FactorLabel.Text = $"{(string)FindResource("UpscaleFactor")} (x{FactorSlider.Value:F1}):";
        }

        private void UpdateInterpolationAndDevices()
        {
            bool isRu = currentLangKey == "Русский";
            var interp = isRu ? new[] { "Lanczos", "Бикубическая", "Сосед", "ИИ (EDSR x2)" } : new[] { "Lanczos", "Bicubic", "Nearest", "AI (EDSR x2)" };
            var devices = isRu ? new[] { "ЦПУ (CPU)", "NVIDIA (CUDA)", "AMD/Intel (OpenCL)" } : new[] { "CPU", "NVIDIA (CUDA)", "AMD/Intel (OpenCL)" };

            int prevM = MethodCombo.SelectedIndex;
            MethodCombo.Items.Clear();
            foreach (var s in interp) MethodCombo.Items.Add(s);
            MethodCombo.SelectedIndex = Math.Max(0, prevM);

            int prevD = DeviceCombo.SelectedIndex;
            DeviceCombo.Items.Clear();
            foreach (var s in devices) DeviceCombo.Items.Add(s);
            DeviceCombo.SelectedIndex = Math.Max(0, prevD);
        }

        private void LangCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LangCombo == null) return;
            currentLangKey = (LangCombo.SelectedIndex == 1) ? "Русский" : "English";
            SwitchLanguage(currentLangKey == "Русский" ? "ru-RU" : "en-US");
        }

        private void ThemeBtn_Click(object sender, RoutedEventArgs e)
        {
            currentThemeMode = currentThemeMode switch
            {
                ThemeMode.System => ThemeMode.Light,
                ThemeMode.Light => ThemeMode.Dark,
                _ => ThemeMode.System
            };
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            bool useDark = currentThemeMode switch
            {
                ThemeMode.Dark => true,
                ThemeMode.Light => false,
                _ => IsSystemInDarkMode()
            };

            ThemeBtn.Content = currentThemeMode switch { ThemeMode.Dark => "🌙", ThemeMode.Light => "☀️", _ => "🌓" };
            var themeName = useDark ? "Dark" : "Light";
            var dict = new ResourceDictionary { Source = new Uri($"Resources/Themes/{themeName}.xaml", UriKind.Relative) };

            for (int i = 0; i < Resources.MergedDictionaries.Count; i++)
            {
                if (Resources.MergedDictionaries[i].Source.OriginalString.Contains("Themes/"))
                {
                    Resources.MergedDictionaries.RemoveAt(i);
                    break;
                }
            }
            Resources.MergedDictionaries.Add(dict);
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

        private void SelectFileBtn_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "Video files (*.mp4;*.avi;*.mov)|*.mp4;*.avi;*.mov|All files (*.*)|*.*";
            if (openFileDialog.ShowDialog() == true)
            {
                selectedPath = openFileDialog.FileName;
                UpdateLabels();
            }
        }

        private void FactorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (FactorLabel != null) UpdateLabels();
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
            StatusLabel.Text = (string)FindResource("Processing");
            ProgBar.Value = 0;
            PercLabel.Text = "0%";
            ETALabel.Text = "";

            cts = new CancellationTokenSource();
            var progress = new Progress<ProgressInfo>(info =>
            {
                ProgBar.Value = info.Percentage;
                PercLabel.Text = $"{(int)info.Percentage}%";
                ETALabel.Text = $"{(string)FindResource("RemainingTime")}: {info.RemainingTime}";
            });

            try
            {
                await Task.Run(() => UpscaleLogic(selectedPath, tempOutputPath, factor, methodIdx, deviceIdx, progress, cts.Token), cts.Token);
                StatusLabel.Text = "Muxing audio...";
                await Task.Run(() => MuxAudio(selectedPath, tempOutputPath, finalOutputPath, cts.Token), cts.Token);
                StatusLabel.Text = (string)FindResource("Success");
                MessageBox.Show((string)FindResource("Success") + "\nSaved to: " + finalOutputPath);
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex.InnerException is OperationCanceledException)
            {
                StatusLabel.Text = (string)FindResource("Cancelled");
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
                    double msPerFrame = sw.ElapsedMilliseconds / (double)currentFrame;
                    double remainingMs = msPerFrame * (totalFrames - currentFrame);
                    TimeSpan t = TimeSpan.FromMilliseconds(remainingMs);
                    string eta = t.TotalHours >= 1 ? $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes:D2}:{t.Seconds:D2}";
                    progress.Report(new ProgressInfo { Percentage = (double)currentFrame / totalFrames * 100.0, RemainingTime = eta });
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
    }
}
