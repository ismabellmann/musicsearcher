(() => {
  "use strict";

  const root = document.documentElement;
  const themeButton = document.querySelector("#theme-toggle");
  const themes = ["system", "light", "dark"];
  const themeLabels = { system: "System", light: "Light", dark: "Dark" };
  let eventSource;
  let toastTimer;

  function setTheme(theme) {
    root.dataset.theme = theme;
    localStorage.setItem("musicsync-theme", theme);
    themeButton.textContent = `◐ Theme: ${themeLabels[theme]}`;
  }

  setTheme(localStorage.getItem("musicsync-theme") || "system");
  themeButton?.addEventListener("click", () => {
    const current = themes.indexOf(root.dataset.theme);
    setTheme(themes[(current + 1) % themes.length]);
  });

  function toast(message) {
    const element = document.querySelector("#toast");
    if (!element) return;
    element.textContent = message;
    element.style.display = "block";
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { element.style.display = "none"; }, 2800);
  }

  async function api(path, options = {}) {
    const response = await fetch(path, {
      credentials: "same-origin",
      headers: { "Content-Type": "application/json", ...(options.headers || {}) },
      ...options
    });
    let body = null;
    try { body = await response.json(); } catch { /* Empty response. */ }
    if (response.status === 401 || response.status === 403) {
      await showAuth();
      throw new Error(body?.msg || "Authentication required.");
    }
    if (!response.ok) throw new Error(body?.msg || `Request failed (${response.status}).`);
    return body?.data ?? body;
  }

  function showAuth() {
    if (document.querySelector("#auth-modal")) return Promise.resolve();
    return new Promise(resolve => {
      const dialog = document.createElement("dialog");
      dialog.id = "auth-modal";
      dialog.className = "modal";
      dialog.innerHTML = `<form method="dialog" id="auth-form" style="padding:22px">
        <span class="eyebrow" id="auth-eyebrow">Private service</span>
        <h2 id="auth-title">Sign in</h2>
        <p id="auth-description" style="margin:8px 0 18px">Use your MusicSync account.</p>
        <input id="auth-user" required minlength="3" maxlength="50" placeholder="Username" autocomplete="username" style="width:100%;margin-bottom:8px">
        <input id="auth-password" required minlength="12" type="password" placeholder="Password" autocomplete="current-password" style="width:100%">
        <p id="auth-error" class="hint" style="color:var(--danger);min-height:18px"></p>
        <div class="modal-actions"><button class="primary-button" type="submit">Continue</button></div>
      </form>`;
      document.body.append(dialog);
      dialog.showModal();
      const form = dialog.querySelector("form");
      form.addEventListener("submit", async event => {
        event.preventDefault();
        const status = await fetch("/api/auth/status", { credentials: "same-origin" }).then(result => result.json());
        const endpoint = status.data?.setupComplete ? "/api/auth/login" : "/api/auth/setup";
        const payload = { username: form.querySelector("#auth-user").value, password: form.querySelector("#auth-password").value };
        try {
          const response = await fetch(endpoint, {
            method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload)
          });
          const result = await response.json();
          if (!response.ok) throw new Error(result.msg || "Authentication failed.");
          dialog.close();
          dialog.remove();
          resolve();
          await refreshAll();
        } catch (error) {
          form.querySelector("#auth-error").textContent = error.message;
        }
      });
    });
  }

  function showView(view) {
    document.querySelectorAll(".view").forEach(item => item.classList.toggle("active", item.dataset.page === view));
    document.querySelectorAll(".nav-button").forEach(item => item.classList.toggle("active", item.dataset.view === view));
    const title = document.querySelector("#page-title");
    if (title) title.textContent = view[0].toUpperCase() + view.slice(1);
    document.querySelector("#sidebar")?.classList.remove("open");
    if (view === "activity") startEvents(); else stopEvents();
    if (view === "activity") loadJobs();
    if (view === "library") loadLibrary();
    if (view === "settings") loadSettings();
  }

  document.querySelectorAll(".nav-button").forEach(button => button.addEventListener("click", () => showView(button.dataset.view)));
  document.querySelector("#mobile-menu")?.addEventListener("click", () => document.querySelector("#sidebar")?.classList.toggle("open"));

  function renderResults(results) {
    const target = document.querySelector("#search-results");
    target.innerHTML = results.length ? results.map((track, index) => `<article class="card result">
      <div class="cover">${index % 2 ? "◉" : "♫"}</div>
      <div class="details"><div class="title">${escapeHtml(track.title)}</div><div class="subtitle">${escapeHtml(track.artistName || "Unknown artist")} · ${escapeHtml(track.albumTitle || "Single")}</div></div>
      <div class="actions"><span class="badge success">MusicBrainz</span><button class="small-button" data-download='${escapeAttribute(JSON.stringify(track))}'>Download</button></div>
    </article>`).join("") : '<div class="empty">No matching tracks found.</div>';
    target.querySelectorAll("[data-download]").forEach(button => button.addEventListener("click", async () => {
      try { await api("/api/downloads", { method: "POST", body: button.dataset.download ? JSON.stringify({ track: JSON.parse(button.dataset.download) }) : "{}" }); toast("Track added to queue."); }
      catch (error) { toast(error.message); }
    }));
  }

  document.querySelector("#search-button")?.addEventListener("click", async () => {
    const query = document.querySelector("#search-input").value.trim();
    if (!query) return toast("Enter a search term.");
    try {
      const type = document.querySelector(".search-box select").value.slice(0, -1).toLowerCase();
      const result = await api(`/api/search?type=${encodeURIComponent(type)}&q=${encodeURIComponent(query)}`);
      renderResults(result || []);
    } catch (error) { toast(error.message); }
  });

  async function loadJobs() {
    try {
      const result = await api("/api/downloads?page=1&pageSize=50");
      const target = document.querySelector("[data-page=activity] .cards");
      target.innerHTML = (result.items || []).map(job => `<article class="card job">
        <div class="cover">♫</div><div class="details"><div class="title">${escapeHtml(job.trackTitle)}</div><div class="subtitle">${escapeHtml(job.artistName)} · Job #${job.jobId}</div></div>
        <span class="badge ${job.jobStatus === "Done" ? "success" : job.jobStatus === "Failed" ? "danger" : "warning"}">${escapeHtml(job.jobStatus)}</span>
        ${["Done", "Failed", "Cancelled"].includes(job.jobStatus) ? "" : `<button class="small-button" data-cancel="${job.jobId}">Cancel</button>`}
      </article>`).join("") || '<div class="empty">No jobs yet.</div>';
      target.querySelectorAll("[data-cancel]").forEach(button => button.addEventListener("click", async () => {
        try { await api(`/api/downloads/${button.dataset.cancel}/cancel`, { method: "POST", body: "{}" }); toast("Cancellation requested."); await loadJobs(); }
        catch (error) { toast(error.message); }
      }));
    } catch (error) { toast(error.message); }
  }

  function startEvents() {
    if (eventSource) return;
    eventSource = new EventSource("/api/events");
    ["job.created", "job.claimed", "job.searching", "job.ready_for_tagging", "job.downloading", "job.tagging", "job.done", "job.failed", "job.cancelled"].forEach(type => {
      eventSource.addEventListener(type, event => { try { toast(JSON.parse(event.data).message); } catch { /* Ignore malformed event. */ } loadJobs(); });
    });
    eventSource.onerror = () => { eventSource.close(); eventSource = null; };
  }
  function stopEvents() { if (eventSource) { eventSource.close(); eventSource = null; } }

  async function loadLibrary() {
    try {
      const result = await api("/api/library?page=1&pageSize=50");
      const target = document.querySelector("[data-page=library] .cards");
      target.innerHTML = (result.items || []).map(file => `<article class="card file"><div class="cover">♫</div><div class="details"><div class="title">${escapeHtml(file.title)}</div><div class="subtitle">${escapeHtml(file.artistName)} · ${escapeHtml(file.albumTitle || "Single")} · ${formatBytes(file.sizeBytes)}</div></div><span class="badge neutral">${escapeHtml(file.origin)}</span><button class="small-button" data-delete="${file.fileId}">Delete</button></article>`).join("") || '<div class="empty">Your library is empty.</div>';
      target.querySelectorAll("[data-delete]").forEach(button => button.addEventListener("click", async () => {
        if (!confirm("Delete this file from the library?")) return;
        try { await api(`/api/library/${button.dataset.delete}`, { method: "DELETE" }); toast("File deleted."); await loadLibrary(); }
        catch (error) { toast(error.message); }
      }));
    } catch (error) { toast(error.message); }
  }

  async function loadSettings() {
    try {
      const settings = await api("/api/settings");
      const hint = document.querySelector("[data-page=settings] .cards article:nth-child(2) .hint");
      if (hint) hint.textContent = `ListenBrainz token: ${settings.listenBrainzConfigured ? "configured" : "not configured"}. Secrets are never displayed.`;
    } catch (error) { toast(error.message); }
  }

  document.querySelector("#save-token")?.addEventListener("click", async () => {
    const token = document.querySelector("#listenbrainz-token").value;
    try { await api("/api/settings", { method: "PUT", body: JSON.stringify({ listenBrainzToken: token }) }); document.querySelector("#listenbrainz-token").value = ""; toast("Token status updated."); await loadSettings(); }
    catch (error) { toast(error.message); }
  });
  document.querySelector("#generate-secret")?.addEventListener("click", async () => {
    try { await api("/api/settings/secrets/generate", { method: "POST", body: JSON.stringify({ name: "ApplicationSecret" }) }); toast("Application secret generated and encrypted."); await loadSettings(); }
    catch (error) { toast(error.message); }
  });
  document.querySelector("#logout-all")?.addEventListener("click", async () => {
    try { await api("/api/auth/logout-all", { method: "POST", body: "{}" }); toast("All sessions revoked."); await showAuth(); }
    catch (error) { toast(error.message); }
  });

  document.querySelector("#playlist-preview")?.addEventListener("click", async () => {
    const url = document.querySelector("#playlist-url").value.trim();
    try { const preview = await api("/api/playlists/preview", { method: "POST", body: JSON.stringify({ url }) }); toast(`Preview created with ${preview.items?.length || 0} items.`); }
    catch (error) { toast(error.message); }
  });

  document.querySelector("#logout")?.addEventListener("click", async () => {
    try { await api("/api/auth/logout", { method: "POST", body: "{}" }); await showAuth(); } catch (error) { toast(error.message); }
  });

  function formatBytes(value) { if (!value) return "0 B"; const units = ["B", "KB", "MB", "GB"]; let index = 0; let size = value; while (size >= 1024 && index < units.length - 1) { size /= 1024; index++; } return `${size.toFixed(index ? 1 : 0)} ${units[index]}`; }
  function escapeHtml(value) { return String(value ?? "").replace(/[&<>'"]/g, character => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" }[character])); }
  function escapeAttribute(value) { return escapeHtml(value); }

  async function refreshAll() {
    try { const status = await api("/api/auth/status"); if (!status.signedIn) return showAuth(); } catch { return showAuth(); }
  }

  const initialView = location.hash.slice(1) || "search";
  showView(["search", "activity", "library", "playlists", "settings"].includes(initialView) ? initialView : "search");
  refreshAll();
})();
