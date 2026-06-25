using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using OpenCvSharp;
using OpenCvSharp.DnnSuperRes;

namespace VideoUpscalerVS
{
    // Simple localization container
    public class Localization
    {
        public string Title { get; set; } = "";
        public string SelectLang { get; set; } = "";
        public string SelectTheme { get; set; } = "";
        public string UploadVideo { get; set; } = "";
        public string UpscaleFactor { get; set; } = "";
        public string Interpolation { get; set; } = "";
        public string StartButton { get; set; } = "";
        public string Processing { get; set; } = "";
        public string Success { get; set; } = "";
        public List<string> Themes { get; set; } = new();
        public List<string> InterpMethods { get; set; } = new();
    }

    class Program
    {
        private static Dictionary<string, Localization> Languages = new Dictionary<string, Localization>
        {
            ["English"] = new Localization
            {
                Title = "Video Upscaler",
                SelectLang = "Select Language",
                SelectTheme = "Select Theme",
                UploadVideo = "Select a video file",
                UpscaleFactor = "Upscale Factor",
                Interpolation = "Interpolation Method",
                StartButton = "Start Upscaling",
                Processing = "Processing video...",
                Success = "Video upscaled successfully!",
                Themes = new List<string> { "Light", "Dark" },
                InterpMethods = new List<string> { "Lanczos (High Quality)", "Bicubic", "Nearest Neighbor", "AI (EDSR x2)" }
            },
            ["Русский"] = new Localization
            {
                Title = "Видео Апскейлер",
                SelectLang = "Выберите язык",
                SelectTheme = "Выберите тему",
                UploadVideo = "Выберите видео файл",
                UpscaleFactor = "Коэффициент масштабирования",
                Interpolation = "Метод интерполяции",
                StartButton = "Начать апскейл",
                Processing = "Обработка видео...",
                Success = "Видео успешно масштабировано!",
                Themes = new List<string> { "Светлая", "Темная" },
                InterpMethods = new List<string> { "Lanczos (Высокое качество)", "Бикубическая", "Ближайший сосед", "ИИ (EDSR x2)" }
            }
        };

        [STAThread]
        static void Main(string[] args)
        {
            // For a Visual Studio project, this would typically be a WPF application.
            // This Program.cs demonstrates the logic that would be used in a WPF backend.

            Console.WriteLine("--- Video Upscaler CLI Backend (Visual Studio Ready) ---");
            Console.WriteLine("In Visual Studio, you would use a WPF window with buttons and sliders.");
            Console.WriteLine("This script contains the core processing logic.");

            // Example call
            // UpscaleVideo("input.mp4", "output.mp4", 2.0, 0);
        }

        public static void UpscaleVideo(string inputPath, string outputPath, double factor, int methodIdx)
        {
            using var capture = new VideoCapture(inputPath);
            if (!capture.IsOpened()) throw new Exception("Could not open video file.");

            int width = capture.FrameWidth;
            int height = capture.FrameHeight;
            double fps = capture.Fps;
            int fourcc = VideoWriter.FourCC('M', 'P', '4', 'V'); // or 'H','2','6','4'

            int newWidth = (int)(width * factor);
            int newHeight = (int)(height * factor);

            using var writer = new VideoWriter(outputPath, fourcc, fps, new OpenCvSharp.Size(newWidth, newHeight));
            using var frame = new Mat();
            using var upscaled = new Mat();

            DnnSuperResImpl? sr = null;
            if (methodIdx == 3)
            {
                sr = new DnnSuperResImpl();
                if (File.Exists("models/EDSR_x2.pb"))
                {
                    sr.ReadModel("models/EDSR_x2.pb");
                    sr.SetModel("edsr", 2);
                }
                else
                {
                    methodIdx = 0; // Fallback
                }
            }

            while (capture.Read(frame))
            {
                if (frame.Empty()) break;

                if (methodIdx == 3 && sr != null)
                {
                    sr.Upsample(frame, upscaled);
                    if (factor != 2.0)
                    {
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
                    Cv2.Resize(frame, upscaled, new OpenCvSharp.Size(newWidth, newHeight), 0, 0, flag);
                }

                writer.Write(upscaled);
            }

            sr?.Dispose();
        }
    }
}
