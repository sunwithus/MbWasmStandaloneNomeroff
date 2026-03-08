(function () {
    var beepAudio = null;
    window.audio = {
        playBeep: function () {
            try {
                if (!beepAudio) {
                    beepAudio = new Audio('audio/beep.wav');
                }
                beepAudio.currentTime = 0;
                beepAudio.play().catch(function (e) { console.warn('Beep play failed', e); });
            } catch (e) {
                console.warn('Beep init failed', e);
            }
        }
    };
})();
