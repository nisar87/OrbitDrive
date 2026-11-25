// MyOrbitDrive live prices. No third-party scripts, no tracking, no storage.
// Prices: the values printed in the page are the fallback. When the service answers, the live prices (set by the owner) replace them: dollars first,
// rupees beside them, "≈" when the rupee amount follows the exchange rate. Nothing is sent to the service and nothing is stored.
(function livePrices() {
  const spots = document.querySelectorAll('[data-plan][data-months]');
  const limitSpots = document.querySelectorAll('[data-limits]');
  if ((!spots.length && !limitSpots.length) || !('fetch' in window)) return;
  const size = (b) => (b >= 1073741824 ? +(b / 1073741824).toFixed(1) + ' GB' : +(b / 1048576).toFixed(0) + ' MB');
  const count = (n, one, many, none) => (typeof n === 'number' && n >= 0 ? n + ' ' + (n === 1 ? one : many) : none);
  const limitsText = (l) => {
    const num = (v) => typeof v === 'number' && Number.isFinite(v) && v >= 0;
    if (!num(l.pcs) || !num(l.bytesPerFile)) return null;                      // an answer that does not look right keeps the printed text
    const parts = ['up to ' + count(l.pcs, 'PC', 'PCs', 'unlimited PCs'), count(l.devices, 'paired device', 'paired devices', 'unlimited devices'),
      num(l.filesPerDay) ? l.filesPerDay + ' files per day' : 'unlimited files', 'up to ' + size(l.bytesPerFile) + ' per file'];
    if (num(l.bytesPerDay)) parts.push('up to ' + size(l.bytesPerDay) + ' per day');
    const text = parts.join(', ');
    return text.charAt(0).toUpperCase() + text.slice(1);
  };
  const usd = (n) => '$' + (Number.isInteger(n) ? n : n.toFixed(2));
  fetch('https://relay.myorbitdrive.app/v1/plans', { cache: 'no-store', credentials: 'omit', referrerPolicy: 'no-referrer' })
    .then((r) => (r.ok ? r.json() : null))
    .then((data) => {
      if (!data || !Array.isArray(data.plans)) return;
      // Limits come from the service too, so a change the owner makes in the console is what the site says. The text in the page is the fallback.
      limitSpots.forEach((el) => {
        const plan = data.plans.find((p) => p.id === el.dataset.limits);
        const text = plan && plan.limits ? limitsText(plan.limits) : null;
        if (text) el.textContent = text;
      });
      spots.forEach((el) => {
        const plan = data.plans.find((p) => p.id === el.dataset.plan);
        const price = plan && Array.isArray(plan.prices) ? plan.prices.find((x) => x.months === Number(el.dataset.months)) : null;
        if (!price || typeof price.usd !== 'number' || typeof price.pkr !== 'number' || !(price.usd > 0) || !(price.pkr > 0)) return;
        el.textContent = usd(price.usd) + ' (' + (price.pkrApprox ? '≈ ' : '') + 'PKR ' + Math.round(price.pkr).toLocaleString('en-US') + ')';
      });
    })
    .catch(() => {});
})();
