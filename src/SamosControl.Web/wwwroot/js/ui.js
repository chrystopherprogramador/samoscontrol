(function () {
  document.querySelectorAll('[data-autosubmit]').forEach(function (e) { e.addEventListener('change', function () { e.form.submit(); }); });
  document.querySelectorAll('[data-select]').forEach(function (e) { e.addEventListener('click', function () { e.select(); }); });
})();
