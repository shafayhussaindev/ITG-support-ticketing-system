// Applied before first paint so a dark-mode user never sees a white flash.
//
// A separate file rather than an inline <script> so the Content-Security-Policy can
// forbid inline script outright. Allowing 'unsafe-inline' for one nine-line function,
// or maintaining a hash of it that silently breaks the page when the whitespace
// changes, both cost more than one cached request.
(function () {
  try {
    var stored = localStorage.getItem('sts.theme');
    var prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
    document.documentElement.dataset.theme = stored || (prefersDark ? 'dark' : 'light');
  } catch (e) {
    document.documentElement.dataset.theme = 'light';
  }
})();
