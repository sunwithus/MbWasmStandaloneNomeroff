// Файл: изображение или видео для распознавания
window.fileReader = {
    _videoId: null,
    _dotNetRef: null,

    /** Загрузка видео из base64 (после InputFile в C#). Обходит проблемы с createObjectURL из динамического input. */
    loadVideoFromBase64(base64, contentType, videoElementId, dotNetRef) {
        var video = document.getElementById(videoElementId);
        if (!video) { dotNetRef.invokeMethodAsync('OnVideoError', 'Video element not found'); return; }
        try {
            var binary = atob(base64);
            var bytes = new Uint8Array(binary.length);
            for (var i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
            var blob = new Blob([bytes], { type: contentType || 'video/mp4' });
            var url = URL.createObjectURL(blob);
            video.src = url;
            video.onloadedmetadata = function () {
                dotNetRef.invokeMethodAsync('OnVideoReady', video.duration);
            };
            video.onerror = function () {
                var msg = 'Ошибка воспроизведения видео.';
                if (video.error) {
                    if (video.error.code === 3) msg = 'Неподдерживаемый кодек. Перекодируйте в H.264 (AVC) в контейнере MP4.';
                    else if (video.error.code === 4) msg = 'Браузер не поддерживает этот MP4 (часто — видео в HEVC/H.265). Перекодируйте в H.264 (AVC).';
                }
                dotNetRef.invokeMethodAsync('OnVideoError', msg);
            };
        } catch (e) {
            dotNetRef.invokeMethodAsync('OnVideoError', 'Ошибка: ' + e.message);
        }
    },

    showFilePicker(videoElementId, dotNetRef) {
        this._videoId = videoElementId;
        this._dotNetRef = dotNetRef;
        var input = document.createElement('input');
        input.type = 'file';
        input.accept = 'image/*,video/*';
        input.style.display = 'none';
        input.onchange = function (ev) {
            var file = ev.target.files && ev.target.files[0];
            if (!file) return;
            if (file.type.indexOf('image/') === 0) {
                var reader = new FileReader();
                reader.onload = function () {
                    var dataUrl = reader.result;
                    var base64 = dataUrl.indexOf(',') >= 0 ? dataUrl.split(',')[1] : dataUrl;
                    dotNetRef.invokeMethodAsync('OnImageSelected', base64);
                };
                reader.readAsDataURL(file);
            } else {
                var video = document.getElementById(videoElementId);
                if (!video) { dotNetRef.invokeMethodAsync('OnVideoError', 'Video element not found'); return; }
                // Добавляем диагностику перед загрузкой
                console.log('Loading video file:', file.name, file.type, file.size);

                var url = URL.createObjectURL(file);
                video.src = url;

                video.onloadedmetadata = function () {
                    console.log('Video metadata loaded:', {
                        duration: video.duration,
                        width: video.videoWidth,
                        height: video.videoHeight,
                        type: file.type
                    });
                    dotNetRef.invokeMethodAsync('OnVideoReady', video.duration);
                };

                video.onerror = function () {
                    console.error('Video load error:', video.error);
                    // Более информативное сообщение
                    var errorMsg = 'Ошибка загрузки видео';
                    if (video.error) {
                        switch (video.error.code) {
                            case 1: errorMsg = 'Прерывание загрузки'; break;
                            case 2: errorMsg = 'Сетевая ошибка'; break;
                            case 3: errorMsg = 'Декодирование не удалось (неподдерживаемый формат?)'; break;
                            case 4: errorMsg = 'Файл не найден'; break;
                        }
                    }
                    dotNetRef.invokeMethodAsync('OnVideoError', errorMsg + ` [${file.type}]`);
                };

                video.onloadeddata = function () {
                    console.log('Video data loaded, readyState:', video.readyState);
                };
            }
            document.body.removeChild(input);
        };
        document.body.appendChild(input);
        input.click();
    },


    getVideoFrameAt(videoElementId, timeSeconds, maxWidth) {
        var video = document.getElementById(videoElementId);
        if (!video) {
            console.error('Video element not found:', videoElementId);
            return null;
        }

        // Проверка готовности видео
        if (video.readyState < 2) {
            console.warn('Video not ready, readyState:', video.readyState);
            // 0=NOTHING, 1=METADATA, 2=CURRENT_DATA, 3=FUTURE_DATA, 4=ENOUGH_DATA
            return null;
        }

        // Проверка на ошибку воспроизведения
        if (video.error) {
            console.error('Video error:', video.error);
            return null;
        }

        maxWidth = maxWidth || 1280;
        var canvas = document.createElement('canvas');
        var w = video.videoWidth, h = video.videoHeight;

        if (w === 0 || h === 0) {
            console.error('Invalid video dimensions:', w, 'x', h);
            return null;
        }

        if (w > maxWidth) {
            h = Math.round(h * maxWidth / w);
            w = maxWidth;
        }
        canvas.width = w;
        canvas.height = h;
        var ctx = canvas.getContext('2d');

        return new Promise(function (resolve, reject) {
            // Сохраняем текущее время для восстановления после seek
            var originalTime = video.currentTime;

            const onSeeked = function () {
                try {
                    ctx.drawImage(video, 0, 0, w, h);
                    var dataUrl = canvas.toDataURL('image/jpeg', 0.85);
                    var base64 = dataUrl.indexOf(',') >= 0 ? dataUrl.split(',')[1] : dataUrl;

                    // Восстанавливаем время воспроизведения
                    video.currentTime = originalTime;
                    video.onseeked = null;
                    resolve(base64);
                } catch (e) {
                    console.error('Canvas draw error:', e);
                    video.onseeked = null;
                    reject(e);
                }
            };

            video.onseeked = onSeeked;
            video.onerror = function (e) {
                console.error('Seek error:', video.error);
                video.onseeked = null;
                reject(video.error);
            };

            // Выполняем seek
            video.currentTime = timeSeconds;
        });
    },
};

/** Скачать текст как файл (для watchlist) */
window.watchlist = {
    clickFileInput(elementId) {
        var el = document.getElementById(elementId);
        if (el) el.click();
    },
    downloadAsFile(content, filename) {
        var blob = new Blob([content], { type: 'text/plain;charset=utf-8' });
        var url = URL.createObjectURL(blob);
        var a = document.createElement('a');
        a.href = url;
        a.download = filename || 'watchlist.txt';
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    }
};
