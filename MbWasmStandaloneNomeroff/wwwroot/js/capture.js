// capture.js
window.camera = {
    async listVideoDevices() {
        try {
            const devices = await navigator.mediaDevices.enumerateDevices();
            return devices
                .filter(d => d.kind === 'videoinput')
                .map(d => ({ deviceId: d.deviceId, label: d.label || ('Камера ' + (d.deviceId.slice(0, 8))) }));
        } catch (err) {
            console.error('listVideoDevices', err);
            return [];
        }
    },

    async startVideo(videoElementId, deviceId) {
        const video = document.getElementById(videoElementId);
        if (!video) return { success: false, error: 'Video element not found' };
        try {
            const constraints = {
                video: {
                    width: { ideal: 1920 },
                    height: { ideal: 1080 },
                    facingMode: deviceId ? undefined : "environment"
                }
            };
            if (deviceId) constraints.video.deviceId = { exact: deviceId };
            const stream = await navigator.mediaDevices.getUserMedia(constraints);
            video.srcObject = stream;
            await video.play();
            return { success: true };
        } catch (err) {
            console.error('Camera error:', err);
            return { success: false, error: err.message };
        }
    },

    captureFrame(videoElementId, maxWidth = 1280) {
        const video = document.getElementById(videoElementId);
        if (!video || !video.videoWidth) return null;
        const canvas = document.createElement('canvas');
        const scale = maxWidth / video.videoWidth;
        canvas.width = maxWidth;
        canvas.height = video.videoHeight * scale;
        const ctx = canvas.getContext('2d');
        ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
        const dataUrl = canvas.toDataURL('image/jpeg', 0.85);
        return dataUrl.indexOf(',') >= 0 ? dataUrl.split(',')[1] : dataUrl;
    },

    stopVideo(videoElementId) {
        const video = document.getElementById(videoElementId);
        if (video && video.srcObject) {
            video.srcObject.getTracks().forEach(track => track.stop());
            video.srcObject = null;
        }
    }
};

window.gps = {
    async getCurrentPosition() {
        return new Promise((resolve, reject) => {
            if (!navigator.geolocation) {
                reject("Geolocation not supported");
                return;
            }
            navigator.geolocation.getCurrentPosition(
                pos => resolve({
                    latitude: pos.coords.latitude,
                    longitude: pos.coords.longitude,
                    accuracy: pos.coords.accuracy
                }),
                err => reject(err.message),
                { enableHighAccuracy: true, timeout: 5000 }
            );
        });
    }
};
