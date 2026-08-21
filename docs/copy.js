// Copy buttons for .promptbox blocks — the only script the docs use.
// Progressive: without it the prompts are still selectable text.
document.querySelectorAll('.promptbox').forEach(function (box) {
  var btn = box.querySelector('.copy');
  var pre = box.querySelector('pre code');
  if (!btn || !pre) return;
  btn.addEventListener('click', function () {
    var text = pre.textContent;
    var done = function () {
      btn.textContent = 'Copied';
      btn.classList.add('done');
      setTimeout(function () { btn.textContent = 'Copy'; btn.classList.remove('done'); }, 1600);
    };
    var fallback = function () {
      var ta = document.createElement('textarea');
      ta.value = text;
      ta.setAttribute('readonly', '');
      ta.style.position = 'absolute';
      ta.style.left = '-9999px';
      document.body.appendChild(ta);
      ta.select();
      try { document.execCommand('copy'); done(); } catch (e) { btn.textContent = 'Select manually'; }
      document.body.removeChild(ta);
    };
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(done, fallback);
    } else {
      fallback();
    }
  });
});
