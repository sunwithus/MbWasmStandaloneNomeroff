Python 3.12.4
Требования
•	Windows 10/11
•	Git (установлен и добавлен в PATH)
•	Visual C++ Redistributable (скачать)
•	NVIDIA GPU + CUDA 11.8 (опционально, для CPU используйте другую команду)


git clone https://github.com/ria-com/nomeroff-net.git
cd nomeroff-net

git clone https://github.com/ria-com/nomeroff-net.git
cd nomeroff-net

python -m venv venv
venv\Scripts\activate

python -m pip install --upgrade pip
python -m pip install "setuptools>=60.0.0,<70.0.0" wheel

python -m pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu118

python -m pip install -r requirements.txt
python -m pip install -r requirements-api.txt

python test.py
python main.py



##### Проверка работоспособности
(venv) D:\_ASR_Numbers\nomeroff-net>python -c "import torch, pytorch_lightning, cv2, nomeroff_net; print('✅ Всё OK')"
✅ Всё OK

(venv) D:\_ASR_Numbers\nomeroff-net>python --version
Python 3.12.4

(venv) D:\_ASR_Numbers\nomeroff-net>pip show setuptools pytorch-lightning torch | findstr Version
Version: 69.5.1
Version: 1.8.6
Version: 2.7.1+cu118
