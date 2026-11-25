// MyOrbitDrive live prices. No third-party scripts, no tracking, no storage.
// Prices: the values printed in the page are the fallback. When the service answers, the live prices (set by the owner) replace them: dollars first,
// rupees beside them, "≈" when the rupee amount follows the exchange rate. Nothing is sent to the service and nothing is stored.
(function livePrices() {
  const spots = document.querySelectorAll('[data-plan][data-months]');
  if (!spots.length || !('fetch' in window)) return;
  const usd = (n) => '$' + (Number.isInteger(n) ? n : n.toFixed(2));
  fetch('https://relay.myorbitdrive.app/v1/plans', { cache: 'no-store', credentials: 'omit', referrerPolicy: 'no-referrer' })
    .then((r) => (r.ok ? r.json() : null))
    .then((data) => {
      if (!data || !Array.isArray(data.plans)) return;
      spots.forEach((el) => {
        const plan = data.plans.find((p) => p.id === el.dataset.plan);
        const price = plan && Array.isArray(plan.prices) ? plan.prices.find((x) => x.months === Number(el.dataset.months)) : null;
        if (!price || typeof price.usd !== 'number' || typeof price.pkr !== 'number' || !(price.usd > 0) || !(price.pkr > 0)) return;
        el.textContent = usd(price.usd) + ' (' + (price.pkrApprox ? '≈ ' : '') + 'PKR ' + Math.round(price.pkr).toLocaleString('en-US') + ')';
      });
    })
    .catch(() => {});
})();
