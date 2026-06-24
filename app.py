import streamlit as st
import cv2
import numpy as np
import tempfile
import os
from moviepy.video.io.VideoFileClip import VideoFileClip

# Localization
LANGUAGES = {
    "English": {
        "title": "Video Upscaler",
        "select_lang": "Select Language",
        "select_theme": "Select Theme",
        "upload_video": "Upload a video file",
        "upscale_factor": "Upscale Factor",
        "interpolation": "Interpolation Method",
        "start_button": "Start Upscaling",
        "processing": "Processing video...",
        "success": "Video upscaled successfully!",
        "download_button": "Download Upscaled Video",
        "themes": {"Light": "Light", "Dark": "Dark"},
        "interp_methods": ["Lanczos (High Quality)", "Bicubic", "Nearest Neighbor"],
    },
    "Русский": {
        "title": "Видео Апскейлер",
        "select_lang": "Выберите язык",
        "select_theme": "Выберите тему",
        "upload_video": "Загрузите видео файл",
        "upscale_factor": "Коэффициент масштабирования",
        "interpolation": "Метод интерполяции",
        "start_button": "Начать апскейл",
        "processing": "Обработка видео...",
        "success": "Видео успешно масштабировано!",
        "download_button": "Скачать масштабированное видео",
        "themes": {"Светлая": "Light", "Темная": "Dark"},
        "interp_methods": ["Lanczos (Высокое качество)", "Бикубическая", "Ближайший сосед"],
    }
}

# Theme CSS
THEMES = {
    "Light": """
        <style>
        .stApp {
            background-color: white;
            color: black;
        }
        </style>
    """,
    "Dark": """
        <style>
        .stApp {
            background-color: #0E1117;
            color: white;
        }
        </style>
    """
}

def upscale_frame(frame, factor, method_idx):
    methods = [cv2.INTER_LANCZOS4, cv2.INTER_CUBIC, cv2.INTER_NEAREST]
    height, width = frame.shape[:2]
    new_width = int(width * factor)
    new_height = int(height * factor)
    return cv2.resize(frame, (new_width, new_height), interpolation=methods[method_idx])

def process_video(input_path, output_path, upscale_factor, method_idx):
    clip = VideoFileClip(input_path)

    # moviepy's resized() handles both frame resizing and metadata (size) update.
    # However, it uses PIL for resizing by default. To use OpenCV Lanczos,
    # we can use image_transform and then manually set the size.

    def process_frame(frame):
        return upscale_frame(frame, upscale_factor, method_idx)

    new_width = int(clip.w * upscale_factor)
    new_height = int(clip.h * upscale_factor)

    # We use image_transform and then explicitly set the new size
    new_clip = clip.image_transform(process_frame)
    # Actually, image_transform is enough, but we MUST update size.
    new_clip.size = (new_width, new_height)

    new_clip.write_videofile(output_path, codec="libx264", audio_codec="aac", fps=clip.fps, logger=None)
    clip.close()
    new_clip.close()

def main():
    st.set_page_config(page_title="Video Upscaler", layout="centered")

    # Session State for Language and Theme
    if 'lang' not in st.session_state:
        st.session_state.lang = "English"
    if 'theme_key' not in st.session_state:
        st.session_state.theme_key = "Light"

    # Sidebar for settings
    with st.sidebar:
        st.session_state.lang = st.selectbox("Language / Язык", list(LANGUAGES.keys()),
                                             index=list(LANGUAGES.keys()).index(st.session_state.lang))
        lang_data = LANGUAGES[st.session_state.lang]

        theme_labels = list(lang_data["themes"].keys())
        selected_theme_label = st.selectbox(lang_data["select_theme"], theme_labels,
                                            index=0 if st.session_state.theme_key == "Light" else 1)
        st.session_state.theme_key = lang_data["themes"][selected_theme_label]

    # Apply Theme
    st.markdown(THEMES[st.session_state.theme_key], unsafe_allow_html=True)

    st.title(lang_data["title"])

    uploaded_file = st.file_uploader(lang_data["upload_video"], type=["mp4", "avi", "mov"])

    if uploaded_file is not None:
        col1, col2 = st.columns(2)
        with col1:
            upscale_factor = st.select_slider(lang_data["upscale_factor"], options=[1.5, 2.0, 3.0, 4.0], value=2.0)
        with col2:
            interpolation = st.selectbox(lang_data["interpolation"], lang_data["interp_methods"])
            method_idx = lang_data["interp_methods"].index(interpolation)

        if st.button(lang_data["start_button"]):
            with st.spinner(lang_data["processing"]):
                tfile = tempfile.NamedTemporaryFile(delete=False, suffix='.mp4')
                tfile.write(uploaded_file.read())
                tfile.close()

                output_path = tempfile.NamedTemporaryFile(delete=False, suffix='.mp4').name

                try:
                    process_video(tfile.name, output_path, upscale_factor, method_idx)

                    st.success(lang_data["success"])
                    with open(output_path, "rb") as f:
                        st.download_button(
                            label=lang_data["download_button"],
                            data=f,
                            file_name="upscaled_video.mp4",
                            mime="video/mp4"
                        )
                except Exception as e:
                    st.error(f"Error: {e}")
                finally:
                    if os.path.exists(tfile.name):
                        os.remove(tfile.name)
                    # Note: output_path is not deleted here to allow download.
                    # In a real app, a cleanup mechanism (like a cron or background task) would be needed.

if __name__ == "__main__":
    main()
