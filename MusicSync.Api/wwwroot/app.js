(() => {
  "use strict";

  const root = document.documentElement;
  const themeButton = document.querySelector("#theme-toggle");
  const themes = ["system", "light", "dark"];
  const themeLabels = { system: "System", light: "Light", dark: "Dark" };
  let eventSource;
  let toastTimer;
  let lastSearchType = "track";
  let playlistPreview;

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
      dialog.setAttribute("aria-modal", "true");
      dialog.innerHTML = `<form id="auth-form" style="padding:22px">
        <span class="eyebrow" id="auth-eyebrow">Authentication required</span>
        <h2 id="auth-title">Sign in</h2>
        <p id="auth-description" style="margin:8px 0 18px">Use your MusicSync account to continue.</p>
        <input id="auth-user" required minlength="3" maxlength="50" placeholder="Username" autocomplete="username" style="width:100%;margin-bottom:8px">
        <input id="auth-password" required minlength="12" type="password" placeholder="Password" autocomplete="current-password" style="width:100%">
        <p id="auth-error" class="hint" style="color:var(--danger);min-height:18px"></p>
        <div class="modal-actions"><button class="primary-button" type="submit">Continue</button></div>
      </form>`;
      document.body.append(dialog);
      dialog.addEventListener("cancel", event => event.preventDefault());
      fetch("/api/auth/status", { credentials: "same-origin" })
        .then(response => response.json())
        .then(status => {
          const setup = !status.data?.setupComplete;
          dialog.querySelector("#auth-eyebrow").textContent = setup ? "First-time setup" : "Private service";
          dialog.querySelector("#auth-title").textContent = setup ? "Create your account" : "Sign in to MusicSync";
          dialog.querySelector("#auth-description").textContent = setup
            ? "Create the single account used to protect this private service."
            : "Sign in to continue to your music library.";
          dialog.querySelector("#auth-password").autocomplete = setup ? "new-password" : "current-password";
          dialog.querySelector("#auth-form button[type=submit]").textContent = setup ? "Create account" : "Sign in";
        })
        .catch(() => { dialog.querySelector("#auth-error").textContent = "Could not check the setup status."; });
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
    if (view === "analytics") loadAnalytics();
  }

  document.querySelectorAll(".nav-button").forEach(button => button.addEventListener("click", () => showView(button.dataset.view)));
  document.querySelector("#mobile-menu")?.addEventListener("click", () => document.querySelector("#sidebar")?.classList.toggle("open"));

  function renderResults(results) {
    const target = document.querySelector("#search-results");
    const action = lastSearchType === "album" ? "Preview album" : lastSearchType === "artist" ? "Preview top tracks" : "Download";
    target.innerHTML = results.length ? results.map((track, index) => `<article class="card result">
      <div class="cover">${index % 2 ? "◉" : "♫"}</div>
      <div class="details"><div class="title">${escapeHtml(track.title)}</div><div class="subtitle">${escapeHtml(track.artistName || "Unknown artist")} · ${escapeHtml(track.albumTitle || "Single")}</div></div>
      <div class="actions"><span class="badge success">MusicBrainz</span><button class="small-button" data-result='${escapeAttribute(JSON.stringify(track))}'>${action}</button></div>
    </article>`).join("") : '<div class="empty">No matching tracks found.</div>';
    target.querySelectorAll("[data-result]").forEach(button => button.addEventListener("click", async () => {
      const track = JSON.parse(button.dataset.result);
      const originalLabel = button.textContent;
      try {
        if (lastSearchType === "track") {
          await api("/api/downloads", { method: "POST", body: JSON.stringify({ track }) });
          toast("Track added to queue.");
        } else if (lastSearchType === "album") {
          button.disabled = true;
          button.innerHTML = '<span class="spinner" aria-hidden="true"></span> Loading…';
          showView("playlists");
          document.querySelector("#playlist-items").innerHTML = '<div class="loading"><span class="spinner" aria-hidden="true"></span><span>Loading album recordings…</span></div>';
          const preview = await api(`/api/playlists/album/${track.recordingMbid}/preview`, { method: "POST", body: "{}" });
          openPlaylistPreview(preview);
        } else {
          button.disabled = true;
          button.innerHTML = '<span class="spinner" aria-hidden="true"></span> Loading…';
          showView("playlists");
          document.querySelector("#playlist-items").innerHTML = '<div class="loading"><span class="spinner" aria-hidden="true"></span><span>Loading artist top tracks…</span></div>';
          const preview = await api(`/api/playlists/artist/${track.recordingMbid}/preview?artistName=${encodeURIComponent(track.artistName || track.title)}&count=10`, { method: "POST", body: "{}" });
          openPlaylistPreview(preview);
        }
      }
      catch (error) {
        if (lastSearchType !== "track") document.querySelector("#playlist-items").innerHTML = `<div class="empty">Preview failed. ${escapeHtml(error.message)}</div>`;
        toast(error.message);
      } finally {
        button.disabled = false;
        button.textContent = originalLabel;
      }
    }));
  }

  async function runSearch() {
    const query = document.querySelector("#search-input").value.trim();
    if (!query) return toast("Enter a search term.");
    const button = document.querySelector("#search-button");
    const results = document.querySelector("#search-results");
    const originalLabel = button.textContent;
    button.disabled = true;
    button.textContent = "Searching…";
    results.innerHTML = '<div class="loading"><span class="spinner" aria-hidden="true"></span><span>Searching MusicBrainz…</span></div>';
    try {
      const type = document.querySelector(".search-box select").value;
      lastSearchType = type;
      if (type === "youtube") {
        const preview = await api("/api/youtube/preview", { method: "POST", body: JSON.stringify({ url: query }) });
        renderYouTubePreview(preview);
        return;
      }
      const result = await api(`/api/search?type=${encodeURIComponent(type)}&q=${encodeURIComponent(query)}`);
      renderResults(result || []);
    } catch (error) {
      results.innerHTML = `<div class="empty">Search failed. ${escapeHtml(error.message)}</div>`;
      toast(error.message);
    } finally {
      button.disabled = false;
      button.textContent = originalLabel;
    }
  }

  document.querySelector("#search-button")?.addEventListener("click", runSearch);
  document.querySelector("#search-input")?.addEventListener("keydown", event => {
    if (event.key === "Enter") {
      event.preventDefault();
      runSearch();
    }
  });

  function renderYouTubePreview(preview) {
    const target = document.querySelector("#search-results");
    target.innerHTML = `<article class="card result"><div class="cover">▶</div><div class="details"><div class="title">${escapeHtml(preview.title)}</div><div class="subtitle">${escapeHtml(preview.artistName || preview.uploader || "Unknown artist")} · ${formatDuration(preview.durationMs)}</div></div><span class="badge neutral">YouTube</span></article>` +
      ((preview.candidates || []).map(candidate => `<article class="card result"><div class="details"><div class="title">${escapeHtml(candidate.title)}</div><div class="subtitle">${escapeHtml(candidate.artistName)} · ${Math.round(candidate.score * 100)}% match</div></div><span class="badge ${candidate.confident ? "success" : "warning"}">${candidate.confident ? "Match" : "Review"}</span><button class="small-button" data-youtube-candidate="${candidate.recordingMbid}">Queue</button></article>`).join("") || '<div class="empty">No confident MusicBrainz candidates found.</div>');
    target.querySelectorAll("[data-youtube-candidate]").forEach(button => button.addEventListener("click", async () => {
      button.disabled = true;
      try { await api("/api/youtube/enqueue", { method: "POST", body: JSON.stringify({ url: preview.sourceUrl, recordingMbid: button.dataset.youtubeCandidate }) }); toast("YouTube video added to queue."); }
      catch (error) { toast(error.message); button.disabled = false; }
    }));
  }

  async function loadJobs() {
    try {
      const [result, analytics] = await Promise.all([
        api("/api/downloads?page=1&pageSize=50"),
        api("/api/analytics/summary?topArtistLimit=1")
      ]);
      const target = document.querySelector("[data-page=activity] .cards");
      const jobs = result.items || [];
      const activeStatuses = new Set(["Queued", "Resolving", "Searching", "Approved", "Downloading", "Tagging", "ReadyForTagging"]);
      const jobStatuses = analytics.jobsByStatus || {};
      document.querySelector("#activity-in-progress").textContent = Object.entries(jobStatuses).filter(([status]) => activeStatuses.has(status)).reduce((total, [, count]) => total + count, 0);
      document.querySelector("#activity-completed").textContent = analytics.completedJobs ?? 0;
      document.querySelector("#activity-review").textContent = analytics.needsReviewCount ?? 0;
      target.innerHTML = jobs.map(job => `<article class="card job">
        <div class="cover">♫</div><div class="details"><div class="title">${escapeHtml(job.trackTitle)}</div><div class="subtitle">${escapeHtml(job.artistName)} · Job #${job.jobId}</div></div>
        <span class="badge ${job.jobStatus === "Done" ? "success" : job.jobStatus === "Failed" ? "danger" : "warning"}">${escapeHtml(job.jobStatus)}</span>
        ${job.jobStatus === "NeedsReview" ? `<button class="small-button" data-review="${job.jobId}">Review</button>` : ""}
        ${["Done", "Failed", "Cancelled"].includes(job.jobStatus) ? "" : `<button class="small-button" data-cancel="${job.jobId}">Cancel</button>`}
      </article>`).join("") || '<div class="empty">No jobs yet.</div>';
      target.querySelectorAll("[data-cancel]").forEach(button => button.addEventListener("click", async () => {
        try { await api(`/api/downloads/${button.dataset.cancel}/cancel`, { method: "POST", body: "{}" }); toast("Cancellation requested."); await loadJobs(); }
        catch (error) { toast(error.message); }
      }));
      target.querySelectorAll("[data-review]").forEach(button => button.addEventListener("click", () => reviewJob(button.dataset.review)));
    } catch (error) { toast(error.message); }
  }

  async function reviewJob(jobId) {
    try {
      const job = await api(`/api/downloads/${jobId}`);
      const candidates = job.candidates || [];
      const dialog = document.createElement("dialog");
      dialog.className = "modal";
      dialog.innerHTML = `<form method="dialog" style="padding:22px"><h2>Review match</h2><p style="margin:8px 0 14px">${escapeHtml(job.trackTitle)} · ${escapeHtml(job.artistName)}</p><div class="cards">${candidates.map(candidate => `<label class="card result" style="cursor:pointer"><input type="radio" name="candidate" value="${candidate.candidateId}" ${candidate.rank === 1 ? "checked" : ""}><div class="details"><div class="title">${escapeHtml(candidate.title)}</div><div class="subtitle">${Math.round(candidate.score * 100)}% · ${escapeHtml(candidate.reason || "No reason provided")}</div></div></label>`).join("") || '<div class="empty">No candidates were stored.</div>'}</div><div class="modal-actions"><button class="ghost-button" value="cancel" style="width:auto">Cancel</button><button class="primary-button" id="approve-candidate" value="approve">Approve</button></div></form>`;
      document.body.append(dialog); dialog.showModal();
      dialog.querySelector("#approve-candidate")?.addEventListener("click", async event => {
        event.preventDefault();
        const selected = dialog.querySelector("input[name=candidate]:checked");
        if (!selected) return toast("Choose a candidate first.");
        try { await api(`/api/downloads/${jobId}/approve`, { method: "POST", body: JSON.stringify({ candidateId: Number(selected.value) }) }); dialog.close(); toast("Candidate approved."); await loadJobs(); }
        catch (error) { toast(error.message); }
      });
      dialog.addEventListener("close", () => dialog.remove());
    } catch (error) { toast(error.message); }
  }

  function startEvents() {
    if (eventSource) return;
    eventSource = new EventSource("/api/events");
    ["job.created", "job.claimed", "job.searching", "job.ready_for_tagging", "job.downloading", "job.tagging", "job.done", "job.failed", "job.cancelled"].forEach(type => {
      eventSource.addEventListener(type, event => { try { toast(JSON.parse(event.data).message); } catch { /* Ignore malformed event. */ } loadJobs(); });
    });
    eventSource.onerror = () => { toast("Activity connection interrupted; reconnecting…"); };
  }
  function stopEvents() { if (eventSource) { eventSource.close(); eventSource = null; } }

  async function loadLibrary() {
    try {
      const q = document.querySelector("#library-filter")?.value.trim() || "";
      const origin = document.querySelector("#library-origin")?.value || "";
      const result = await api(`/api/library?page=1&pageSize=50&q=${encodeURIComponent(q)}&origin=${encodeURIComponent(origin)}`);
      const target = document.querySelector("[data-page=library] .cards");
      const items = await Promise.all((result.items || []).map(async file => ({ file, rating: await api(`/api/ratings/track/track:${file.recordingMbid}`).catch(() => ({ value: 0 })) })));
      target.innerHTML = items.map(({ file, rating }) => `<article class="card file"><div class="cover">♫</div><div class="details"><div class="title">${escapeHtml(file.title)}</div><div class="subtitle">${escapeHtml(file.artistName)} · ${escapeHtml(file.albumTitle || "Single")} · ${formatBytes(file.sizeBytes)}</div><div class="subtitle">MusicBrainz: ${formatScore(file.musicBrainzRating)} · My score: ${formatScore(file.personalRating)}</div><div class="rating-stars" aria-label="My rating ${file.personalRating} of 5">${[1,2,3,4,5].map(value => `<button class="rating-star ${value <= file.personalRating ? "on" : ""}" data-rating="${file.recordingMbid}" data-value="${value}" aria-label="Rate ${value} out of 5">★</button>`).join("")}</div></div><span class="badge neutral">${escapeHtml(file.origin)}</span><div class="actions"><button class="small-button" data-detail="${file.fileId}">Details</button><button class="small-button" data-delete="${file.fileId}">Delete</button></div></article>`).join("") || '<div class="empty">Your library is empty.</div>';
      target.querySelectorAll("[data-rating]").forEach(button => button.addEventListener("click", async () => {
        try { await api(`/api/ratings/track/track:${button.dataset.rating}`, { method: "PUT", body: JSON.stringify({ value: Number(button.dataset.value), notes: null }) }); toast("Rating saved."); await loadLibrary(); }
        catch (error) { toast(error.message); }
      }));
      target.querySelectorAll("[data-detail]").forEach(button => button.addEventListener("click", () => showLibraryDetail(button.dataset.detail)));
      target.querySelectorAll("[data-delete]").forEach(button => button.addEventListener("click", async () => {
        if (!confirm("Delete this file from the library?")) return;
        try { await api(`/api/library/${button.dataset.delete}`, { method: "DELETE" }); toast("File deleted."); await loadLibrary(); }
        catch (error) { toast(error.message); }
      }));
    } catch (error) { toast(error.message); }
  }

  async function showLibraryDetail(id) {
    try {
      const file = await api(`/api/library/${id}`);
      const dialog = document.createElement("dialog");
      dialog.className = "modal";
      dialog.innerHTML = `<form method="dialog" style="padding:22px"><h2>${escapeHtml(file.title)}</h2><p style="margin-top:8px">${escapeHtml(file.artistName)} · ${escapeHtml(file.albumTitle || "Single")}</p><p class="hint" style="margin-top:12px">${escapeHtml(file.relativePath)}<br>${formatBytes(file.sizeBytes)} · ${escapeHtml(file.origin)}<br>Recording: ${escapeHtml(file.recordingMbid)}</p><div class="modal-actions"><button class="primary-button">Close</button></div></form>`;
      document.body.append(dialog); dialog.showModal(); dialog.addEventListener("close", () => dialog.remove());
    } catch (error) { toast(error.message); }
  }

  async function loadSettings() {
    try {
      const [settings, health] = await Promise.all([api("/api/settings"), fetch("/api/health", { credentials: "same-origin" }).then(response => response.json())]);
      const hint = document.querySelector("[data-page=settings] .cards article:nth-child(2) .hint");
      if (hint) hint.textContent = `ListenBrainz token: ${settings.listenBrainzConfigured ? "configured" : "not configured"}. Secrets are never displayed.`;
      const serviceStatus = document.querySelector("#service-status");
      if (serviceStatus) serviceStatus.textContent = health.data?.status === "ok" ? "Healthy · SQLite · Worker online" : "Service health unavailable";
    } catch (error) { toast(error.message); }
  }

  async function loadAnalytics() {
    try {
      const summary = await api("/api/analytics/summary");
      const values = document.querySelectorAll("#analytics-stats .stat-value");
      values[0].textContent = summary.totalFiles;
      values[1].textContent = formatBytes(summary.totalBytes);
      values[2].textContent = `${Math.round(summary.successRate * 100)}%`;
      document.querySelector("#top-artists").innerHTML = (summary.topArtists || []).map(artist => `<div class="result"><div class="details"><div class="title">${escapeHtml(artist.artistName)}</div><div class="subtitle">${artist.fileCount} files</div></div></div>`).join("") || '<div class="empty">No artist data yet.</div>';
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
  document.querySelector("#change-password")?.addEventListener("click", () => {
    const dialog = document.createElement("dialog");
    dialog.className = "modal";
    dialog.innerHTML = `<form style="padding:22px"><h2>Change password</h2><p class="hint">Use at least 12 characters.</p><input id="current-password" required type="password" placeholder="Current password" autocomplete="current-password" style="width:100%;margin-top:12px"><input id="new-password" required minlength="12" type="password" placeholder="New password" autocomplete="new-password" style="width:100%;margin-top:8px"><p class="hint" id="password-error" style="color:var(--danger);min-height:18px"></p><div class="modal-actions"><button type="button" class="ghost-button" id="password-cancel" style="width:auto">Cancel</button><button type="submit" class="primary-button">Update password</button></div></form>`;
    document.body.append(dialog); dialog.showModal();
    dialog.querySelector("#password-cancel").addEventListener("click", () => dialog.close());
    dialog.querySelector("form").addEventListener("submit", async event => {
      event.preventDefault();
      try { await api("/api/auth/password", { method: "POST", body: JSON.stringify({ currentPassword: dialog.querySelector("#current-password").value, newPassword: dialog.querySelector("#new-password").value }) }); dialog.close(); toast("Password updated."); }
      catch (error) { dialog.querySelector("#password-error").textContent = error.message; }
    });
    dialog.addEventListener("close", () => dialog.remove());
  });
  document.querySelector("#logout-all")?.addEventListener("click", async () => {
    try { await api("/api/auth/logout-all", { method: "POST", body: "{}" }); toast("All sessions revoked."); await showAuth(); }
    catch (error) { toast(error.message); }
  });

  document.querySelector("#playlist-preview")?.addEventListener("click", async () => {
    const url = document.querySelector("#playlist-url").value.trim();
    try { const preview = await api("/api/playlists/preview", { method: "POST", body: JSON.stringify({ url }) }); openPlaylistPreview(preview); toast(`Preview created with ${preview.items?.length || 0} items.`); }
    catch (error) { toast(error.message); }
  });

  function openPlaylistPreview(preview) {
    playlistPreview = preview;
    const target = document.querySelector("#playlist-items");
    target.innerHTML = (preview.items || []).map(item => {
      const options = (item.candidates || []).map(candidate => `<option value="${candidate.recordingMbid}" ${candidate.recordingMbid === item.selectedRecordingMbid ? "selected" : ""}>${escapeHtml(candidate.title)} · ${escapeHtml(candidate.artistName)} (${Math.round(candidate.score * 100)}%)</option>`).join("");
      return `<article class="card result"><input type="checkbox" data-playlist-item="${item.itemId}" ${item.status === "Matched" ? "checked" : ""} style="width:18px;height:18px"><div class="details"><div class="title">${escapeHtml(item.title)}</div><div class="subtitle">${escapeHtml(item.artistName)} · position ${item.position}</div></div><span class="badge ${item.status === "Matched" ? "success" : item.status === "NeedsReview" ? "warning" : "neutral"}">${escapeHtml(item.status)}</span>${options ? `<select data-playlist-selection="${item.itemId}" style="max-width:260px"><option value="">Choose candidate</option>${options}</select>` : ""}</article>`;
    }).join("") || '<div class="empty">No preview items.</div>';
  }

  document.querySelector("#playlist-enqueue")?.addEventListener("click", async () => {
    if (!playlistPreview) return toast("Create a preview first.");
    const itemIds = [...document.querySelectorAll("[data-playlist-item]:checked")].map(item => Number(item.dataset.playlistItem));
    const selections = {};
    document.querySelectorAll("[data-playlist-selection]").forEach(select => { if (select.value) selections[select.dataset.playlistSelection] = select.value; });
    try {
      const result = await api(`/api/playlists/${playlistPreview.importId}/enqueue`, { method: "POST", body: JSON.stringify({ itemIds, selections }) });
      toast(`${result.enqueued?.length || 0} tracks added; ${result.skipped?.length || 0} skipped.`);
      const refreshed = await api(`/api/playlists/${playlistPreview.importId}`); openPlaylistPreview(refreshed);
    } catch (error) { toast(error.message); }
  });

  document.querySelector("#library-scan")?.addEventListener("click", async () => {
    try { const result = await api("/api/library/scan", { method: "POST", body: "{}" }); toast(result ? "Library scan complete." : "Library scan complete."); await loadLibrary(); }
    catch (error) { toast(error.message); }
  });
  document.querySelector("#library-filter")?.addEventListener("input", debounce(loadLibrary, 300));
  document.querySelector("#library-origin")?.addEventListener("change", loadLibrary);

  document.querySelector("#logout")?.addEventListener("click", async () => {
    try { await api("/api/auth/logout", { method: "POST", body: "{}" }); await showAuth(); } catch (error) { toast(error.message); }
  });

  function formatBytes(value) { if (!value) return "0 B"; const units = ["B", "KB", "MB", "GB"]; let index = 0; let size = value; while (size >= 1024 && index < units.length - 1) { size /= 1024; index++; } return `${size.toFixed(index ? 1 : 0)} ${units[index]}`; }
  function formatDuration(value) { if (!value) return "Duration unavailable"; const seconds = Math.round(value / 1000); return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`; }
  function formatScore(value) { return value == null || value === 0 ? "—" : `${Number(value).toFixed(1)}/5`; }
  function debounce(callback, delay) { let timer; return (...args) => { clearTimeout(timer); timer = setTimeout(() => callback(...args), delay); }; }
  function escapeHtml(value) { return String(value ?? "").replace(/[&<>'"]/g, character => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" }[character])); }
  function escapeAttribute(value) { return escapeHtml(value); }

  async function refreshAll() {
    try {
      const status = await api("/api/auth/status");
      if (!status.signedIn) return showAuth();
      const user = document.querySelector("#current-user");
      if (user) user.textContent = status.username || "signed-in user";
    } catch { return showAuth(); }
  }

  const initialView = location.hash.slice(1) || "search";
  showView(["search", "activity", "library", "playlists", "analytics", "settings"].includes(initialView) ? initialView : "search");
  refreshAll();
})();
