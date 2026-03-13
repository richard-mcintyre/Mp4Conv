window.dropFileList = {
    initialize: function (element, dotnetRef) {
        element.addEventListener('dragenter', (e) => {
            e.preventDefault();
            dotnetRef.invokeMethodAsync('OnDragEnter');
        });

        element.addEventListener('dragleave', (e) => {
            e.preventDefault();
            if (!element.contains(e.relatedTarget)) {
                dotnetRef.invokeMethodAsync('OnDragLeave');
            }
        });

        element.addEventListener('dragover', (e) => {
            e.preventDefault();
            e.dataTransfer.dropEffect = 'copy';
        });

        element.addEventListener('drop', (e) => {
            e.preventDefault();

            // Firefox exposes full file:/// URIs via text/uri-list; Chrome does not
            const uris = [];
            const uriList = e.dataTransfer.getData('text/uri-list');
            if (uriList) {
                uriList.split('\n')
                    .map(s => s.trim())
                    .filter(s => s && !s.startsWith('#') && s.startsWith('file:///'))
                    .forEach(uri => uris.push(uri));
            }

            // Chrome only exposes file names via dataTransfer.files (no path)
            const fileNames = [];
            for (let i = 0; i < e.dataTransfer.files.length; i++) {
                fileNames.push(e.dataTransfer.files[i].name);
            }

            dotnetRef.invokeMethodAsync('OnFilesDropped', uris, fileNames);
        });
    }
};
