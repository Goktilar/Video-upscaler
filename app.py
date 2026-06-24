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
        "interp_methods": ["Lanczos (High Quality)", "Bicubic", "Nearest Neighbor", "AI (EDSR x2)"],
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
        "interp_methods": ["Lanczos (Высокое качество)", "Бикубическая", "Ближайший сосед", "ИИ (EDSR x2)"],
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

# Initialize Super Resolution model
sr = None
def get_sr_model():
    global sr
    if sr is None:
        sr = cv2.dnn_superres.DnnSuperResImpl_create()
        model_path = "models/EDSR_x2.pb"
        if os.path.exists(model_path):
            sr.readModel(model_path)
            sr.setModel("edsr", 2)
        else:
            sr = None
    return sr

def upscale_frame(frame, factor, method_idx):
    # MoviePy uses RGB, but OpenCV typically uses BGR or RGB depending on the function.
    # dnn_superres and resize work fine with RGB if consistently used.
    # However, for dnn_superres it's better to ensure correct color space if required by model.
    # Most OpenCV models are trained on BGR.

    # Convert RGB (MoviePy) to BGR for OpenCV
    frame_bgr = cv2.cvtColor(frame, cv2.COLOR_RGB2BGR)

    if method_idx == 3:
        model = get_sr_model()
        if model:
            upscaled_bgr = model.upsample(frame_bgr)
            if factor != 2.0:
                h, w = frame.shape[:2]
                upscaled_bgr = cv2.resize(upscaled_bgr, (int(w * factor), int(h * factor)), interpolation=cv2.INTER_LANCZOS4)
            return cv2.cvtColor(upscaled_bgr, cv2.COLOR_BGR2RGB)
        else:
            method_idx = 0

    methods = [cv2.INTER_LANCZOS4, cv2.INTER_CUBIC, cv2.INTER_NEAREST]
    height, width = frame.shape[:2]
    new_width = int(width * factor)
    new_height = int(height * factor)
    upscaled_bgr = cv2.resize(frame_bgr, (new_width, new_height), interpolation=methods[method_idx])

    return cv2.cvtColor(upscaled_bgr, cv2.COLOR_BGR2RGB)

def process_video(input_path, output_path, upscale_factor, method_idx):
    clip = VideoFileClip(input_path)

    def process_frame(frame):
        return upscale_frame(frame, upscale_factor, method_idx)

    new_width = int(clip.w * upscale_factor)
    new_height = int(clip.h * upscale_factor)

    # MoviePy 2.x uses image_transform but some versions/docs might favor fl_image.
    # Looking at the directory from earlier: image_transform was present.
    # I'll use image_transform and manually set size as verified before.
    new_clip = clip.image_transform(process_frame)
    new_clip.size = (new_width, new_height)

    new_clip.write_videofile(output_path, codec="libx264", audio_codec="aac", fps=clip.fps, logger=None)
    clip.close()
    new_clip.close()

def main():
    st.set_page_config(page_title="Video Upscaler", layout="centered")

    if 'lang' not in st.session_state:
        st.session_state.lang = "English"
    if 'theme_key' not in st.session_state:
        st.session_state.theme_key = "Light"

    with st.sidebar:
        st.session_state.lang = st.selectbox("Language / Язык", list(LANGUAGES.keys()),
                                             index=list(LANGUAGES.keys()).index(st.session_state.lang))
        lang_data = LANGUAGES[st.session_state.lang]

        theme_labels = list(lang_data["themes"].keys())
        selected_theme_label = st.selectbox(lang_data["select_theme"], theme_labels,
                                            index=0 if st.session_state.theme_key == "Light" else 1)
        st.session_state.theme_key = lang_data["themes"][selected_theme_label]

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
            if method_idx == 3 and not os.path.exists("models/EDSR_x2.pb"):
                st.error("AI Model file (models/EDSR_x2.pb) not found. Please follow README instructions to download it.")
            else:
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
                        # We can't easily delete output_path here because download_button
                        # triggers after this block finishes.
                        # For now, let's keep it as is, or read into memory.
                        # Reading into memory:
                        # data = open(output_path, "rb").read()
                        # os.remove(output_path)
                        # download_button(data=data)

if __name__ == "__main__":
    main()
