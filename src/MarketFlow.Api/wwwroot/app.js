const productId = document.querySelector("#product-id");
const warehouseId = document.querySelector("#warehouse-id");
const message = document.querySelector("#result-message");
const refreshButton = document.querySelector("#refresh-button");
const operationButtons = [...document.querySelectorAll("form button")];
let stockRequest = 0;

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

function showStock(stock) {
  document.querySelector("#on-hand").textContent = stock?.onHand ?? "—";
  document.querySelector("#reserved").textContent = stock?.reserved ?? "—";
  document.querySelector("#available").textContent = stock?.available ?? "—";
}

async function refreshStock(operationMessage, operationFailed = false) {
  const request = ++stockRequest;
  if (!pairIsValid()) {
    showStock();
    setMessage("Укажите корректные идентификаторы товара и склада.", true);
    return;
  }

  const query = new URLSearchParams({ productId: Number(productId.value), warehouseId: Number(warehouseId.value) });
  try {
    const response = await fetch(`/api/stocks?${query}`);
    if (!response.ok) throw new Error(await readError(response));

    const [stock] = await response.json();
    if (request !== stockRequest) return;
    showStock(stock);
    setMessage(operationMessage ?? (stock ? "Остаток загружен." : "Для этой пары операций пока нет."), operationFailed);
  } catch {
    if (request !== stockRequest) return;
    showStock();
    const refreshError = "Не удалось обновить остаток.";
    setMessage(operationMessage ? `${operationMessage} ${refreshError}` : refreshError, true);
  }
}

async function submitOnce(path, body, successText) {
  if (refreshButton.disabled) return;
  if (!pairIsValid()) {
    setMessage("Сначала укажите корректные идентификаторы товара и склада.", true);
    return;
  }

  ++stockRequest;
  const controls = [productId, warehouseId, refreshButton, ...operationButtons];
  controls.forEach(control => control.disabled = true);
  try {
    let operationMessage = successText;
    let operationFailed = false;
    try {
      const response = await fetch(path, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ productId: Number(productId.value), warehouseId: Number(warehouseId.value), ...body })
      });
      if (!response.ok) {
        operationFailed = true;
        operationMessage = await readError(response);
      }
    } catch {
      operationFailed = true;
      operationMessage = "Не удалось подтвердить результат операции. Проверьте остаток перед повтором.";
    }
    setMessage(operationMessage, operationFailed);
    await refreshStock(operationMessage, operationFailed);
  } finally {
    controls.forEach(control => control.disabled = false);
  }
}

document.querySelector("#supply-form").addEventListener("submit", event => {
  event.preventDefault();
  submitOnce("/api/supplies", {
    reference: document.querySelector("#reference").value,
    quantity: Number(document.querySelector("#supply-quantity").value)
  }, "Приёмка подтверждена.");
});

document.querySelector("#reservation-form").addEventListener("submit", event => {
  event.preventDefault();
  submitOnce("/api/reservations", {
    quantity: Number(document.querySelector("#reservation-quantity").value)
  }, "Резерв создан.");
});

refreshButton.addEventListener("click", () => refreshStock());

[productId, warehouseId].forEach(input => {
  input.addEventListener("input", () => {
    ++stockRequest;
    showStock();
    setMessage("Выберите товар и склад для просмотра остатка.");
  });
  input.addEventListener("change", () => {
    if (pairIsValid() && !refreshButton.disabled) refreshStock();
  });
});
