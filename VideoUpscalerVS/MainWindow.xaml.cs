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
        private double videoDuration = 0;

        public MainWindow()
        {
            InitializeComponent();
            DetectSystemDefaults();
            SystemEvents.UserPreferenceChanged += (s, e) => { if (currentThemeMode == ThemeMode.System) ApplyTheme(); };
        }

        private void DetectSystemDefaults()
        {
            string sysLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            currentLangKey = (sysLang == "ru") ? "Русский" : "English";
            LangCombo.SelectedIndex = (sysLang == "ru") ? 1 : 0;
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
            if (string.IsNullOrEmpty(selectedPath)) FilePathLabel.Text = (string)FindResource("NoFile");
            else FilePathLabel.Text = selectedPath;
            FactorLabel.Text = $"{(string)FindResource("UpscaleFactor")} (x{FactorSlider.Value:F1}):";
        }

        private void UpdateInterpolationAndDevices()
        {
            bool isRu = currentLangKey == "Русский";
            var interp = isRu ? new[] { "Lanczos", "Бикубическая", "Сосед", "ИИ (EDSR x2)" } : new[] { "Lanczos", "Bicubic", "Nearest", "AI (EDSR x2)" };
            var devices = isRu ? new[] { "CPU", "NVIDIA (CUDA)", "AMD/Intel (OpenCL)" } : new[] { "CPU", "NVIDIA (CUDA)", "AMD/Intel (OpenCL)" };

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
            currentThemeMode = currentThemeMode switch { ThemeMode.System => ThemeMode.Light, ThemeMode.Light => ThemeMode.Dark, _ => ThemeMode.System };
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            bool useDark = currentThemeMode switch { ThemeMode.Dark => true, ThemeMode.Light => false, _ => IsSystemInDarkMode() };
            ThemeBtn.Content = currentThemeMode switch { ThemeMode.Dark => "🌙", ThemeMode.Light => "☀️", _ => "🌓" };
            var themeName = useDark ? "Dark" : "Light";
            var dict = new ResourceDictionary { Source = new Uri($"Resources/Themes/{themeName}.xaml", UriKind.Relative) };
            for (int i = 0; i < Resources.MergedDictionaries.Count; i++)
            {
                if (Resources.MergedDictionaries[i].Source.OriginalString.Contains("Themes/")) { Resources.MergedDictionaries.RemoveAt(i); break; }
            }
            Resources.MergedDictionaries.Add(dict);
        }

        private bool IsSystemInDarkMode()
        {
            try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); var val = key?.GetValue("AppsUseLightTheme"); if (val != null) return (int)val == 0; } catch { }
            return false;
        }

        private void SelectFileBtn_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog { Filter = "Video files (*.mp4;*.avi;*.mov)|*.mp4;*.avi;*.mov|All files (*.*)|*.*" };
            if (openFileDialog.ShowDialog() == true)
            {
                selectedPath = openFileDialog.FileName;
                UpdateLabels();
                using var cap = new VideoCapture(selectedPath);
                if (cap.IsOpened())
                {
                    videoDuration = cap.FrameCount / cap.Fps;
                    PreviewSlider.Maximum = Math.Max(0, videoDuration - 1);
                }
            }
        }

        private void FactorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (FactorLabel != null) UpdateLabels(); }

        private void PreviewSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TimeLabel != null)
            {
                TimeSpan t = TimeSpan.FromSeconds(e.NewValue);
                TimeLabel.Text = $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
            }
        }

        private async void StartBtn_Click(object sender, RoutedEventArgs e) => await RunUpscale(false);
        private async void PreviewBtn_Click(object sender, RoutedEventArgs e) => await RunUpscale(true);

        private async Task RunUpscale(bool isPreview)
        {
            if (string.IsNullOrEmpty(selectedPath)) return;

            string baseName = Path.GetFileNameWithoutExtension(selectedPath);
            string dir = isPreview ? Path.GetTempPath() : Path.GetDirectoryName(selectedPath);
            string tempSilent = Path.Combine(Path.GetTempPath(), "upscale_temp_" + Guid.NewGuid().ToString("N") + ".mp4");
            string finalOutput = Path.Combine(dir, baseName + (isPreview ? "_preview.mp4" : "_upscaled.mp4"));

            double factor = FactorSlider.Value;
            int methodIdx = MethodCombo.SelectedIndex;
            int deviceIdx = DeviceCombo.SelectedIndex;
            double startTime = PreviewSlider.Value;

            SetUIEnabled(false);
            ProgressGrid.Visibility = Visibility.Visible;
            StatusLabel.Text = (string)FindResource("Processing");
            ProgBar.Value = 0;
            ETALabel.Text = "";

            cts = new CancellationTokenSource();
            var progress = new Progress<ProgressInfo>(info => { ProgBar.Value = info.Percentage; PercLabel.Text = $"{(int)info.Percentage}%"; ETALabel.Text = $"{(string)FindResource("RemainingTime")}: {info.RemainingTime}"; });

            try
            {
                await Task.Run(() => UpscaleLogic(selectedPath, tempSilent, factor, methodIdx, deviceIdx, progress, cts.Token, isPreview, startTime), cts.Token);

                if (isPreview)
                {
                    StatusLabel.Text = "Creating comparison...";
                    await Task.Run(() => CreateComparison(selectedPath, tempSilent, finalOutput, startTime, cts.Token), cts.Token);
                }
                else
                {
                    StatusLabel.Text = "Muxing audio...";
                    await Task.Run(() => MuxAudio(selectedPath, tempSilent, finalOutput, cts.Token), cts.Token);
                }

                StatusLabel.Text = (string)FindResource("Success");
                if (isPreview) Process.Start(new ProcessStartInfo(finalOutput) { UseShellExecute = true });
                else MessageBox.Show((string)FindResource("Success") + "\nSaved to: " + finalOutput);
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex.InnerException is OperationCanceledException)
            {
                StatusLabel.Text = (string)FindResource("Cancelled");
                if (File.Exists(finalOutput)) try { File.Delete(finalOutput); } catch { }
            }
            catch (Exception ex) { MessageBox.Show("Error: " + ex.Message); }
            finally
            {
                if (File.Exists(tempSilent)) try { File.Delete(tempSilent); } catch { }
                ProgressGrid.Visibility = Visibility.Collapsed;
                SetUIEnabled(true);
                cts?.Dispose();
                cts = null;
            }
        }

        private void SetUIEnabled(bool enabled)
        {
            StartBtn.IsEnabled = PreviewBtn.IsEnabled = SelectFileBtn.IsEnabled = FactorSlider.IsEnabled = MethodCombo.IsEnabled = DeviceCombo.IsEnabled = LangCombo.IsEnabled = ThemeBtn.IsEnabled = PreviewSlider.IsEnabled = enabled;
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e) => cts?.Cancel();

        private void UpscaleLogic(string input, string output, double factor, int methodIdx, int deviceIdx, IProgress<ProgressInfo> progress, CancellationToken token, bool isPreview, double startTime)
        {
            using var capture = new VideoCapture(input);
            if (isPreview) capture.PosFrames = (int)(startTime * capture.Fps);

            int width = capture.FrameWidth, height = capture.FrameHeight;
            double fps = capture.Fps;
            int totalFrames = isPreview ? (int)(15 * fps) : capture.FrameCount;
            if (isPreview) totalFrames = Math.Min(totalFrames, capture.FrameCount - capture.PosFrames);

            int newWidth = (int)(width * factor), newHeight = (int)(height * factor);
            using var writer = new VideoWriter(output, VideoWriter.FourCC('m', 'p', '4', 'v'), fps, new OpenCvSharp.Size(newWidth, newHeight));
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
            for (int i = 0; i < totalFrames; i++)
            {
                token.ThrowIfCancellationRequested();
                if (!capture.Read(frame) || frame.Empty()) break;

                if (methodIdx == 3 && net != null)
                {
                    using var blob = CvDnn.BlobFromImage(frame, 1.0, new OpenCvSharp.Size(frame.Width, frame.Height), new Scalar(), false, false);
                    net.SetInput(blob);
                    using var resultBlob = net.Forward();
                    using var p0 = Mat.FromPixelData(resultBlob.Size(2), resultBlob.Size(3), MatType.CV_32FC1, resultBlob.Ptr(0, 0));
                    using var p1 = Mat.FromPixelData(resultBlob.Size(2), resultBlob.Size(3), MatType.CV_32FC1, resultBlob.Ptr(0, 1));
                    using var p2 = Mat.FromPixelData(resultBlob.Size(2), resultBlob.Size(3), MatType.CV_32FC1, resultBlob.Ptr(0, 2));
                    using var merged = new Mat();
                    Cv2.Merge(new[] { p0, p1, p2 }, merged);
                    merged.ConvertTo(upscaled, MatType.CV_8UC3);
                    if (factor != 2.0) Cv2.Resize(upscaled, upscaled, new OpenCvSharp.Size(newWidth, newHeight), 0, 0, InterpolationFlags.Lanczos4);
                }
                else
                {
                    Cv2.Resize(frame, upscaled, new OpenCvSharp.Size(newWidth, newHeight), 0, 0, methodIdx switch { 1 => InterpolationFlags.Cubic, 2 => InterpolationFlags.Nearest, _ => InterpolationFlags.Lanczos4 });
                }
                writer.Write(upscaled);

                if (i % 5 == 0)
                {
                    double perc = (double)(i + 1) / totalFrames * 100.0;
                    double msPerFrame = sw.ElapsedMilliseconds / (double)(i + 1);
                    TimeSpan t = TimeSpan.FromMilliseconds(msPerFrame * (totalFrames - i));
                    progress.Report(new ProgressInfo { Percentage = perc, RemainingTime = t.TotalHours >= 1 ? $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes:D2}:{t.Seconds:D2}" });
                }
            }
            net?.Dispose();
        }

        private void CreateComparison(string original, string upscaled, string output, double startTime, CancellationToken token)
        {
            // Use FFmpeg to: 1. Extract 15s from original, 2. Resize original to match upscaled height, 3. Stack horizontally
            string args = $"-ss {startTime} -t 15 -i \"{original}\" -i \"{upscaled}\" -filter_complex \"[0:v]scale=-1:oh,setsar=1[v0];[1:v]setsar=1[v1];[v0][v1]hstack=inputs=2\" -c:v libx264 -preset ultrafast -y \"{output}\"";
            RunFFmpeg(args, token);
        }

        private void MuxAudio(string original, string upscaled, string output, CancellationToken token)
        {
            string args = $"-i \"{upscaled}\" -i \"{original}\" -map 0:v -map 1:a? -c:v copy -c:a copy -shortest \"{output}\" -y";
            RunFFmpeg(args, token);
        }

        private void RunFFmpeg(string args, CancellationToken token)
        {
            ProcessStartInfo psi = new ProcessStartInfo("ffmpeg", args) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
            using var p = Process.Start(psi);
            if (p != null) { using (token.Register(() => { try { p.Kill(); } catch { } })) p.WaitForExit(); if (token.IsCancellationRequested) token.ThrowIfCancellationRequested(); }
        }
    }

    public class Localization { public string Title { get; set; } public string NoFile { get; set; } public string UploadVideo { get; set; } public string UpscaleFactor { get; set; } public string Interpolation { get; set; } public string Device { get; set; } public string StartButton { get; set; } public string PreviewButton { get; set; } public string PreviewStartTime { get; set; } public string CancelButton { get; set; } public string Processing { get; set; } public string Success { get; set; } public string Cancelled { get; set; } public string RemainingTime { get; set; } public List<string> InterpMethods { get; set; } public List<string> Devices { get; set; } }
}
