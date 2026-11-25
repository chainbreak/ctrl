"use strict";

const state = { csrf: "", recoveryLogin: false };
const byId = id => document.getElementById(id);
const api = async (path, options = {}) => {
  const method = options.method || "GET";
  const headers = new Headers(options.headers || {});
  headers.set("Accept", "application/json");
  if (options.body !== undefined) headers.set("Content-Type", "application/json");
  if (!["GET", "HEAD", "OPTIONS"].includes(method.toUpperCase())) {
    headers.set("X-CSRF-TOKEN", state.csrf);
  }
  const response = await fetch(`/api${path}`, {
    ...options,
    method,
    headers,
    credentials: "same-origin",
    cache: "no-store"
  });
  if (response.status === 204) return null;
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(data.message || data.title || `Запрос завершился ошибкой (${response.status}).`);
  return data;
};

function showNotice(message, kind = "") {
  const notice = byId("notice");
  notice.textContent = message;
  notice.className = `notice ${kind}`;
  notice.hidden = !message;
}

function showLogin() {
  byId("auth").hidden = false;
  byId("login-form").hidden = false;
  byId("mfa-form").hidden = true;
  byId("mfa-setup").hidden = true;
  byId("dashboard").hidden = true;
  byId("logout").hidden = true;
}

function showMfaLogin() {
  state.recoveryLogin = false;
  byId("auth-title").textContent = "Подтвердите вход";
  byId("login-form").hidden = true;
  byId("mfa-form").hidden = false;
  byId("mfa-label").innerHTML = 'Код приложения-аутентификатора<input name="code" inputmode="numeric" autocomplete="one-time-code" required maxlength="8">';
  byId("recovery-toggle").textContent = "Использовать код восстановления";
  byId("auth-hint").textContent = "Вход ожидает второй фактор.";
}

function showDashboard(user) {
  byId("auth").hidden = true;
  byId("logout").hidden = false;
  byId("identity").textContent = `${user.email} · MFA ${user.twoFactorEnabled ? "включена" : "не настроена"}`;
  byId("search-form").hidden=!user.roles.includes("Admin");
  if (!user.roles.some(r=>["Admin","Support"].includes(r))) {
    showLogin();
    showNotice("У этого аккаунта нет доступа администратора.", "error");
    return;
  }
  if (!user.twoFactorEnabled) {
    byId("mfa-setup").hidden = false;
    byId("dashboard").hidden = true;
    return;
  }
  byId("mfa-setup").hidden = true;
  byId("dashboard").hidden = false;
}

function addField(form, labelText, name, type, value = "") {
  const label = document.createElement("label");
  label.textContent = labelText;
  const input = document.createElement("input");
  input.name = name;
  input.type = type;
  input.value = value;
  input.required = true;
  label.append(input);
  form.append(label);
  return input;
}

function button(text, action, className = "button secondary") {
  const element = document.createElement("button");
  element.type = "button";
  element.className = className;
  element.textContent = text;
  element.addEventListener("click", action);
  return element;
}

function renderUser(user) {
  const card = document.createElement("article");
  card.className = "panel user-card";

  const head = document.createElement("div");
  head.className = "user-head";
  const details = document.createElement("div");
  details.className = "user-details";
  const name = document.createElement("div");
  name.className = "user-name";
  name.textContent = user.handle || user.email;
  const email = document.createElement("div");
  email.className = "muted";
  email.textContent = user.email;
  const status = document.createElement("div");
  status.className = "muted small";
  status.textContent = user.isBanned
    ? `Заблокирован${user.banExpiresAtUtc ? ` до ${new Date(user.banExpiresAtUtc).toLocaleString()}` : " навсегда"}${user.banReason ? ` · ${user.banReason}` : ""}`
    : "Активен";
  details.append(name, email, status);
  const balance = document.createElement("strong");
  balance.textContent = `${user.clicksBalance.toLocaleString("ru-RU")} Кликов`;
  head.append(details, balance);
  card.append(head);

  const actions = document.createElement("div");
  actions.className = "user-actions";
  actions.append(button("Изменить баланс", () => renderAdjustmentForm(card, user)));
  actions.append(user.isBanned
    ? button("Разблокировать", () => renderBanForm(card, user, false))
    : button("Заблокировать", () => renderBanForm(card, user, true), "button danger"));
  card.append(actions);
  return card;
}

function renderBanForm(card, user, shouldBan) {
  const existing = card.querySelector(".inline-form");
  if (existing) existing.remove();
  const form = document.createElement("form");
  form.className = "inline-form";
  addField(form, "Причина (обязательно)", "reason", "text");
  if (shouldBan) {
    const label = document.createElement("label");
    label.textContent = "Срок";
    const select = document.createElement("select");
    select.name = "duration";
    for (const [value, text] of [["24", "24 часа"], ["168", "7 дней"], ["720", "30 дней"], ["permanent", "Навсегда"]]) {
      const option = document.createElement("option");
      option.value = value;
      option.textContent = text;
      select.append(option);
    }
    label.append(select);
    form.append(label);
  }
  const submit = document.createElement("button");
  submit.className = shouldBan ? "button danger" : "button";
  submit.textContent = shouldBan ? "Подтвердить бан" : "Подтвердить разбан";
  form.append(submit);
  form.addEventListener("submit", async event => {
    event.preventDefault();
    const values = new FormData(form);
    try {
      const reason = String(values.get("reason")).trim();
      const duration = shouldBan ? values.get("duration") : null;
      await api(`/admin/users/${encodeURIComponent(user.id)}/ban`, {
        method: shouldBan ? "POST" : "DELETE",
        body: JSON.stringify(shouldBan
          ? { reason, durationHours: duration === "permanent" ? null : Number(duration) }
          : { reason })
      });
      showNotice(shouldBan ? "Пользователь заблокирован." : "Пользователь разблокирован.", "success");
      await searchUsers(byId("query").value);
    } catch (error) {
      showNotice(error.message, "error");
    }
  });
  card.append(form);
}

function renderAdjustmentForm(card, user) {
  const existing = card.querySelector(".inline-form");
  if (existing) existing.remove();
  const form = document.createElement("form");
  form.className = "inline-form";
  addField(form, "Изменение (например, +100 или -20)", "delta", "number");
  addField(form, "Причина (обязательно)", "reason", "text");
  const submit = document.createElement("button");
  submit.className = "button";
  submit.textContent = "Записать операцию";
  form.append(submit);
  form.addEventListener("submit", async event => {
    event.preventDefault();
    const values = new FormData(form);
    const delta = Number(values.get("delta"));
    if (!Number.isSafeInteger(delta) || delta === 0) {
      showNotice("Введите целое ненулевое изменение баланса.", "error");
      return;
    }
    try {
      await api(`/admin/users/${encodeURIComponent(user.id)}/clicks`, {
        method: "POST",
        body: JSON.stringify({
          amountDelta: delta,
          reason: String(values.get("reason")).trim(),
          operationId: crypto.randomUUID()
        })
      });
      showNotice("Баланс изменён; операция записана в журнал.", "success");
      await searchUsers(byId("query").value);
    } catch (error) {
      showNotice(error.message, "error");
    }
  });
  card.append(form);
}

async function searchUsers(query) {
  const results = byId("results");
  results.replaceChildren();
  const users = await api(`/admin/users?q=${encodeURIComponent(query.trim())}`);
  if (users.length === 0) {
    const empty = document.createElement("p");
    empty.className = "muted";
    empty.textContent = "Пользователи не найдены.";
    results.append(empty);
    return;
  }
  for (const user of users) results.append(renderUser(user));
}

async function refreshCsrf() {
    const csrf = await fetch("/api/auth/csrf", { credentials: "same-origin", cache: "no-store" });
    if (!csrf.ok) throw new Error("Не удалось получить CSRF-токен.");
    state.csrf = (await csrf.json()).requestToken;
}
async function initialize() {
  try {
    await refreshCsrf();
    const user = await api("/auth/me");
    showDashboard(user);
  } catch (error) {
    showLogin();
    if (error.message !== "Запрос завершился ошибкой (401).") showNotice(error.message, "error");
  }
}

byId("login-form").addEventListener("submit", async event => {
  event.preventDefault();
  const values = new FormData(event.currentTarget);
  try {
    const result = await api("/auth/login", {
      method: "POST",
      body: JSON.stringify({ email: values.get("email"), password: values.get("password") })
    });
    if (result.requiresTwoFactor) {
      showMfaLogin();
      return;
    }
    await refreshCsrf();
    showDashboard(result.user);
  } catch (error) {
    showNotice(error.message, "error");
  }
});

byId("mfa-form").addEventListener("submit", async event => {
  event.preventDefault();
  const code = String(new FormData(event.currentTarget).get("code")).trim();
  try {
    const route = state.recoveryLogin ? "/auth/login/recovery-code" : "/auth/login/two-factor";
    const result = await api(route, { method: "POST", body: JSON.stringify({ code }) });
    await refreshCsrf();
    showDashboard(result.user);
  } catch (error) {
    showNotice(error.message, "error");
  }
});

byId("recovery-toggle").addEventListener("click", () => {
  state.recoveryLogin = !state.recoveryLogin;
  byId("mfa-label").innerHTML = state.recoveryLogin
    ? 'Одноразовый код восстановления<input name="code" autocomplete="off" required maxlength="32">'
    : 'Код приложения-аутентификатора<input name="code" inputmode="numeric" autocomplete="one-time-code" required maxlength="8">';
  byId("recovery-toggle").textContent = state.recoveryLogin
    ? "Вернуться к приложению-аутентификатору"
    : "Использовать код восстановления";
});

byId("begin-mfa").addEventListener("click", async () => {
  try {
    const setup = await api("/auth/two-factor/setup", { method: "POST", body: "{}" });
    byId("mfa-secret").value = setup.sharedKey;
    byId("mfa-enrollment").hidden = false;
  } catch (error) {
    showNotice(error.message, "error");
  }
});

byId("enable-mfa-form").addEventListener("submit", async event => {
  event.preventDefault();
  const code = String(new FormData(event.currentTarget).get("code")).trim();
  try {
    const result = await api("/auth/two-factor/enable", { method: "POST", body: JSON.stringify({ code }) });
    await refreshCsrf();
    byId("recovery-codes").replaceChildren();
    const heading = document.createElement("strong");
    heading.textContent = "Сохраните recovery-коды в безопасном месте. Повторно они не показываются.";
    const codes = document.createElement("code");
    codes.textContent = result.recoveryCodes.join("\n");
    byId("recovery-codes").append(heading, codes);
    byId("recovery-codes").hidden = false;
    byId("mfa-enrollment").hidden = true;
    showNotice("MFA включена. Сохраните коды, затем войдите снова.", "success");
    await api("/auth/logout", { method: "POST", body: "{}" });
    showLogin();
  } catch (error) {
    showNotice(error.message, "error");
  }
});

byId("search-form").addEventListener("submit", async event => {
  event.preventDefault();
  try {
    await searchUsers(byId("query").value);
  } catch (error) {
    showNotice(error.message, "error");
  }
});

byId("logout").addEventListener("click", async () => {
  try {
    await api("/auth/logout", { method: "POST", body: "{}" });
  } finally {
    await refreshCsrf();
    showLogin();
  }
});

initialize();
