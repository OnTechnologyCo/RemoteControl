'use strict';
self.onmessage = function (ev) {
  var id = ev.data.id;
  createImageBitmap(new Blob([ev.data.bytes], { type: 'image/jpeg' })).then(function (image) {
    self.postMessage({ id: id, image: image }, [image]);
  }, function () { self.postMessage({ id: id }); });
};
