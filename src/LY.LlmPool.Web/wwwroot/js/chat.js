function scrollToBottom(element) {
    if (element) {
        element.scrollTop = element.scrollHeight;
    }
}

// 读取文件为 ArrayBuffer
function readFileAsArrayBuffer(file) {
    return new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => {
            const arrayBuffer = reader.result;
            const uint8Array = new Uint8Array(arrayBuffer);
            resolve(Array.from(uint8Array));
        };
        reader.onerror = () => reject(reader.error);
        reader.readAsArrayBuffer(file);
    });
} 

// 触发下载 data URL 的助手
window.downloadDataUrl = function(url, filename) {
    try {
        const a = document.createElement('a');
        a.href = url;
        a.download = filename || 'download';
        a.style.display = 'none';
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
    } catch (e) {
        console.error('downloadDataUrl failed', e);
    }
}