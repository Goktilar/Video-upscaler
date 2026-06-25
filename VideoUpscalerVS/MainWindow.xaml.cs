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

            int prevIndex = MethodCombo?.SelectedIndex ?? 0;
            MethodCombo.Items.Clear();
            foreach (var m in currentLang.InterpMethods) MethodCombo.Items.Add(m);
            MethodCombo.SelectedIndex = prevIndex >= 0 ? prevIndex : 0;
        }

        private void ThemeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ThemeCombo == null) return;
            string theme = (ThemeCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content.ToString() ?? "Light";

            if (theme == "Dark" || theme == "Темная")
            {
                MainGrid.Background = new SolidColorBrush(Color.FromRgb(14, 17, 23));
                TitleLabel.Foreground = Brushes.White;
                LangLabel.Foreground = Brushes.White;
                ThemeLabel.Foreground = Brushes.White;
                FilePathLabel.Foreground = Brushes.White;
                FactorLabel.Foreground = Brushes.White;
                MethodLabel.Foreground = Brushes.White;
            }
            else
            {
                MainGrid.Background = Brushes.White;
                TitleLabel.Foreground = Brushes.Black;
                LangLabel.Foreground = Brushes.Black;
                ThemeLabel.Foreground = Brushes.Black;
                FilePathLabel.Foreground = Brushes.Black;
                FactorLabel.Foreground = Brushes.Black;
                MethodLabel.Foreground = Brushes.Black;
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
            if (FactorLabel != null)
                FactorLabel.Text = $"{currentLang?.UpscaleFactor ?? "Factor"} (x{e.NewValue:F1}):";
        }

        private async void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(selectedPath)) return;

            string outputPath = Path.Combine(Path.GetDirectoryName(selectedPath),
                Path.GetFileNameWithoutExtension(selectedPath) + "_upscaled.mp4");

            double factor = FactorSlider.Value;
            int methodIdx = MethodCombo.SelectedIndex;

            ProgBar.Visibility = Visibility.Visible;
            StartBtn.IsEnabled = false;
            StatusLabel.Text = currentLang.Processing;

            try
            {
                await Task.Run(() => UpscaleLogic(selectedPath, outputPath, factor, methodIdx));
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

        private void UpscaleLogic(string input, string output, double factor, int methodIdx)
        {
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
                }
                else throw new Exception("AI Model not found at " + modelPath);
            }

            while (capture.Read(frame))
            {
                if (frame.Empty()) break;
                if (methodIdx == 3 && sr != null)
                {
                    sr.Upsample(frame, upscaled);
                    if (factor != 2.0) Cv2.Resize(upscaled, upscaled, new OpenCvSharp.Size(newWidth, newHeight), 0, 0, InterpolationFlags.Lanczos4);
                }
                else
                {
                    InterpolationFlags flag = methodIdx switch { 0 => InterpolationFlags.Lanczos4, 1 => InterpolationFlags.Cubic, 2 => InterpolationFlags.Nearest, _ => InterpolationFlags.Lanczos4 };
                    Cv2.Resize(frame, upscaled, new OpenCvSharp.Size(newWidth, newHeight), 0, 0, flag);
                }
                writer.Write(upscaled);
            }
            sr?.Dispose();
        }

        private Dictionary<string, Localization> Languages = new Dictionary<string, Localization>
        {
            ["English"] = new Localization { Title = "Video Upscaler", SelectLang = "Language:", SelectTheme = "Theme:", UploadVideo = "Select Video File", UpscaleFactor = "Upscale Factor", Interpolation = "Interpolation:", StartButton = "Start Upscaling", Processing = "Processing...", Success = "Done!", InterpMethods = new List<string> { "Lanczos", "Bicubic", "Nearest", "AI (EDSR x2)" } },
            ["Русский"] = new Localization { Title = "Видео Апскейлер", SelectLang = "Язык:", SelectTheme = "Тема:", UploadVideo = "Выбрать видео", UpscaleFactor = "Коэффициент", Interpolation = "Метод:", StartButton = "Начать", Processing = "Обработка...", Success = "Готово!", InterpMethods = new List<string> { "Lanczos", "Бикубическая", "Сосед", "ИИ (EDSR x2)" } }
        };
    }

    public class Localization { public string Title { get; set; } public string SelectLang { get; set; } public string SelectTheme { get; set; } public string UploadVideo { get; set; } public string UpscaleFactor { get; set; } public string Interpolation { get; set; } public string StartButton { get; set; } public string Processing { get; set; } public string Success { get; set; } public List<string> InterpMethods { get; set; } }
}
