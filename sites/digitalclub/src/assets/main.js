// Digital Club site: language switcher, scroll reveal, contact form.
(function () {
  'use strict';
  document.documentElement.classList.add('js');

  // Language switcher: remember the choice and go to that language's page.
  var switcher = document.querySelector('[data-lang-switch]');
  if (switcher) {
    switcher.addEventListener('change', function () {
      try { localStorage.setItem('dc-lang', switcher.value); } catch (e) { /* storage blocked */ }
      window.location.href = '/' + switcher.value + '/' + window.location.hash;
    });
  }
  document.querySelectorAll('[data-lang-link]').forEach(function (a) {
    a.addEventListener('click', function () {
      try { localStorage.setItem('dc-lang', a.getAttribute('data-lang-link')); } catch (e) { /* storage blocked */ }
    });
  });

  // Reveal cards as they scroll into view.
  var items = document.querySelectorAll('.reveal');
  if ('IntersectionObserver' in window) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (en) {
        if (en.isIntersecting) { en.target.classList.add('in'); io.unobserve(en.target); }
      });
    }, { threshold: 0.12 });
    items.forEach(function (el) { io.observe(el); });
  } else {
    items.forEach(function (el) { el.classList.add('in'); });
  }

  // "Suggest a product" pre-selects the topic.
  document.querySelectorAll('[data-topic]').forEach(function (a) {
    a.addEventListener('click', function () {
      var select = document.querySelector('[data-contact-form] select[name="topic"]');
      if (select) select.value = a.getAttribute('data-topic');
    });
  });

  // Contact form → POST JSON to the configured endpoint.
  var form = document.querySelector('[data-contact-form]');
  if (!form) return;
  var status = form.querySelector('.form-status');
  var button = form.querySelector('button[type="submit"]');
  var emailPattern = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

  function show(kind, text) {
    status.className = 'form-status ' + kind;
    status.textContent = text;
  }

  form.addEventListener('submit', function (ev) {
    ev.preventDefault();
    // form.name would be the form's own name attribute, so read fields via namedItem.
    function field(n) { return form.elements.namedItem(n); }
    var data = {
      name: field('name').value.trim(),
      email: field('email').value.trim(),
      company: field('company').value.trim(),
      topic: field('topic').value,
      message: field('message').value.trim(),
      language: field('language').value,
      website: field('website').value, // honeypot
      consent: field('consent').checked
    };
    if (!data.name || !emailPattern.test(data.email) || !data.message || !data.consent) {
      show('err', status.getAttribute('data-invalid'));
      return;
    }

    button.disabled = true;
    button.textContent = button.getAttribute('data-sending');
    show('', '');

    fetch(form.getAttribute('data-endpoint'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(data)
    }).then(function (res) {
      if (!res.ok) throw new Error('HTTP ' + res.status);
      form.reset();
      show('ok', status.getAttribute('data-sent'));
    }).catch(function () {
      show('err', status.getAttribute('data-error'));
    }).finally(function () {
      button.disabled = false;
      button.textContent = button.getAttribute('data-send');
    });
  });
})();
