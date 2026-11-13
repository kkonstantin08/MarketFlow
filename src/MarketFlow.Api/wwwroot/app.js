const productId = document.querySelector("#product-id");
const warehouseId = document.querySelector("#warehouse-id");
const message = document.querySelector("#result-message");
const refreshButton = document.querySelector("#refresh-button");
const operationButtons = [...document.querySelectorAll("form button")];

function pairIsValid() {
  return productId.value && warehouseId.value && productId.checkValidity() && warehouseId.checkValidity();
}

function setMessage(text, isError = false) {
  message.textContent = text;
  message.classList.toggle("error", isError);
}

async function readError(response) {
  const problem = await response.json().catch(() => ({}));
  const reason = problem.detail || problem.title || "Проверьте данные и попробуйте снова.";
  return problem.code ? `${reason} (${problem.code})` : reason;
}

async function refreshStock() {
  if (!pairIsValid()) {
    setMessage("Укажите корректные идентификаторы товара и склада.", true);
    return null;
  }

  const query = new URLSearchParams({ productId: productId.value, warehouseId: warehouseId.value });
  const response = await fetch(`/api/stocks?${query}`);
  if (!response.ok) throw new Error(await readError(response));

  const stocks = await response.json();
  const stock = stocks[0];
  document.querySelector("#on-hand").textContent = stock?.onHand ?? "—";
  document.querySelector("#reserved").textContent = stock?.reserved ?? "—";
  document.querySelector("#available").textContent = stock?.available ?? "—";
  return stock;
}

async function submitOnce(path, body, successText) {
  if (!pairIsValid()) {
    setMessage("Сначала укажите корректные идентификаторы товара и склада.", true);
    return;
  }

  refreshButton.disabled = true;
  operationButtons.forEach(button => button.disabled = true);
  try {
    const response = await fetch(path, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ productId: Number(productId.value), warehouseId: Number(warehouseId.value), ...body })
    });
    if (!response.ok) throw new Error(await readError(response));
    await response.json();
    setMessage(successText);
    await refreshStock();
  } catch (error) {
    setMessage(error.message || "Не удалось выполнить запрос.", true);
    try { await refreshStock(); } catch { /* Keep the operation error visible. */ }
  } finally {
    refreshButton.disabled = false;
    operationButtons.forEach(button => button.disabled = false);
  }
}

document.querySelector("#supply-form").addEventListener("submit", event => {
  event.preventDefault();
  submitOnce("/api/supplies", {
    reference: document.querySelector("#reference").value,
    quantity: Number(document.querySelector("#supply-quantity").value)
  }, "Приёмка подтверждена. Остаток обновлён.");
});

document.querySelector("#reservation-form").addEventListener("submit", event => {
  event.preventDefault();
  submitOnce("/api/reservations", {
    quantity: Number(document.querySelector("#reservation-quantity").value)
  }, "Резерв создан. Остаток обновлён.");
});

refreshButton.addEventListener("click", async () => {
  try {
    const stock = await refreshStock();
    if (stock) setMessage("Остаток загружен.");
    else if (pairIsValid()) setMessage("Для этой пары операций пока нет.");
  } catch (error) {
    setMessage(error.message || "Не удалось загрузить остаток.", true);
  }
});

[productId, warehouseId].forEach(input => input.addEventListener("change", () => {
  if (!productId.value || !warehouseId.value) return;
  refreshButton.click();
}));
