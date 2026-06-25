using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.DnnSuperRes;

namespace VideoUpscalerVS
{
    public partial class MainWindow : Window
    {
        private string selectedPath = "";
        private Localization currentLang;

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
            MainGrid.Background = isDark ? new SolidColorBrush(Color.FromRgb(14, 17, 23)) : Brushes.White;
            var brush = isDark ? Brushes.White : Brushes.Black;

            TitleLabel.Foreground = brush;
            LangLabel.Foreground = brush;
            ThemeLabel.Foreground = brush;
            FilePathLabel.Foreground = brush;
            FactorLabel.Foreground = brush;
            MethodLabel.Foreground = brush;
            DeviceLabel.Foreground = brush;
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

            string outputPath = Path.Combine(Path.GetDirectoryName(selectedPath),
                Path.GetFileNameWithoutExtension(selectedPath) + "_upscaled.mp4");

            double factor = FactorSlider.Value;
            int methodIdx = MethodCombo.SelectedIndex;
            int deviceIdx = DeviceCombo.SelectedIndex;

            ProgBar.Visibility = Visibility.Visible;
            StartBtn.IsEnabled = false;
            StatusLabel.Text = currentLang.Processing;

            try
            {
                await Task.Run(() => UpscaleLogic(selectedPath, outputPath, factor, methodIdx, deviceIdx));
                StatusLabel.Text = currentLang.Success;
                MessageBox.Show(currentLang.Success + "\nSaved to: " + outputPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error: " + ex.Message);
            }
            finally
            {
                ProgBar.Visibility = Visibility.Collapsed;
                StartBtn.IsEnabled = true;
            }
        }

        private void UpscaleLogic(string input, string output, double factor, int methodIdx, int deviceIdx)
        {
            // deviceIdx: 0=CPU, 1=NVIDIA (CUDA), 2=AMD/Intel (OpenCL)

            using var capture = new VideoCapture(input);
            int width = capture.FrameWidth;
            int height = capture.FrameHeight;
            double fps = capture.Fps;
            int fourcc = VideoWriter.FourCC('m', 'p', '4', 'v');

            int newWidth = (int)(width * factor);
            int newHeight = (int)(height * factor);

            using var writer = new VideoWriter(output, fourcc, fps, new OpenCvSharp.Size(newWidth, newHeight));
            using var frame = new Mat();
            using var upscaled = new Mat();

            DnnSuperResImpl? sr = null;
            if (methodIdx == 3)
            {
                sr = new DnnSuperResImpl();
                string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models/EDSR_x2.pb");
                if (File.Exists(modelPath))
                {
                    sr.ReadModel(modelPath);
                    sr.SetModel("edsr", 2);

                    // Optimization for DNN
                    if (deviceIdx == 1) // NVIDIA
                    {
                        sr.SetPreferableBackend(Net.Backend.CUDA);
                        sr.SetPreferableTarget(Net.Target.CUDA);
                    }
                }
                else throw new Exception("AI Model not found at " + modelPath);
            }

            // Enable OpenCL for standard OpenCV functions (like Resize) if requested
            if (deviceIdx == 2) // OpenCL
            {
                Cv2.SetUseOpenCL(true);
            }
            else
            {
                Cv2.SetUseOpenCL(false);
            }

            while (capture.Read(frame))
            {
                if (frame.Empty()) break;

                if (methodIdx == 3 && sr != null)
                {
                    sr.Upsample(frame, upscaled);
                    if (factor != 2.0)
                    {
                        // For standard resize, OpenCL optimization works via UMat automatically if SetUseOpenCL is true
                        Cv2.Resize(upscaled, upscaled, new OpenCvSharp.Size(newWidth, newHeight), 0, 0, InterpolationFlags.Lanczos4);
                    }
                }
                else
                {
                    InterpolationFlags flag = methodIdx switch
                    {
                        0 => InterpolationFlags.Lanczos4,
                        1 => InterpolationFlags.Cubic,
                        2 => InterpolationFlags.Nearest,
                        _ => InterpolationFlags.Lanczos4
                    };

                    if (deviceIdx == 2) // OpenCL
                    {
                        using var uFrame = frame.ToUMat(AccessFlag.Read);
                        using var uUpscaled = new UMat();
                        Cv2.Resize(uFrame, uUpscaled, new OpenCvSharp.Size(newWidth, newHeight), 0, 0, flag);
                        uUpscaled.CopyTo(upscaled);
                    }
                    else
                    {
                        Cv2.Resize(frame, upscaled, new OpenCvSharp.Size(newWidth, newHeight), 0, 0, flag);
                    }
                }
                writer.Write(upscaled);
            }
            sr?.Dispose();
        }

        private Dictionary<string, Localization> Languages = new Dictionary<string, Localization>
        {
            ["English"] = new Localization { Title = "Video Upscaler", SelectLang = "Language:", SelectTheme = "Theme:", UploadVideo = "Select Video File", UpscaleFactor = "Upscale Factor", Interpolation = "Interpolation:", Device = "Device (Optimization):", StartButton = "Start Upscaling", Processing = "Processing...", Success = "Done!", InterpMethods = new List<string> { "Lanczos", "Bicubic", "Nearest", "AI (EDSR x2)" }, Devices = new List<string> { "CPU", "NVIDIA (CUDA)", "AMD/Intel (OpenCL)" } },
            ["Русский"] = new Localization { Title = "Видео Апскейлер", SelectLang = "Язык:", SelectTheme = "Тема:", UploadVideo = "Выбрать видео", UpscaleFactor = "Коэффициент", Interpolation = "Метод:", Device = "Устройство:", StartButton = "Начать", Processing = "Обработка...", Success = "Готово!", InterpMethods = new List<string> { "Lanczos", "Бикубическая", "Сосед", "ИИ (EDSR x2)" }, Devices = new List<string> { "ЦПУ (CPU)", "NVIDIA (CUDA)", "AMD/Intel (OpenCL)" } }
        };
    }

    public class Localization
    {
        public string Title { get; set; }
        public string SelectLang { get; set; }
        public string SelectTheme { get; set; }
        public string UploadVideo { get; set; }
        public string UpscaleFactor { get; set; }
        public string Interpolation { get; set; }
        public string Device { get; set; }
        public string StartButton { get; set; }
        public string Processing { get; set; }
        public string Success { get; set; }
        public List<string> InterpMethods { get; set; }
        public List<string> Devices { get; set; }
    }
}
