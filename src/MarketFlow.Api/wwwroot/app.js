const productSelect = document.querySelector("#product-id");
const warehouseSelect = document.querySelector("#warehouse-id");
const message = document.querySelector("#result-message");
const refreshButton = document.querySelector("#refresh-button");
const actionButtons = [...document.querySelectorAll("form button")];
let stockRequest = 0;

function pairIsValid() {
  return productSelect.value && warehouseSelect.value
    && productSelect.checkValidity() && warehouseSelect.checkValidity();
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

function setOptions(select, items, label) {
  select.replaceChildren(new Option(label, "", true, true));
  for (const item of items)
    select.add(new Option(item.label, item.id));
}

async function loadCatalogs() {
  try {
    // The API caps a page at 100 items; add pagination if demo catalogs outgrow that size.
    const [productsResponse, warehousesResponse] = await Promise.all([
      fetch("/api/products?limit=100"), fetch("/api/warehouses?limit=100")
    ]);
    if (!productsResponse.ok) throw new Error(await readError(productsResponse));
    if (!warehousesResponse.ok) throw new Error(await readError(warehousesResponse));

    const [products, warehouses] = await Promise.all([productsResponse.json(), warehousesResponse.json()]);
    setOptions(productSelect, products.map(product => ({ id: product.id, label: `${product.name} · ${product.sku}` })), "Выберите товар");
    setOptions(warehouseSelect, warehouses.map(warehouse => ({ id: warehouse.id, label: `${warehouse.name} · №${warehouse.id}` })), "Выберите склад");
    setMessage("Выберите товар и склад или создайте их ниже.");
  } catch (error) {
    setMessage(error.message || "Не удалось загрузить каталог.", true);
  }
}

async function createCatalogItem(form, path, select, label) {
  if (!form.reportValidity()) return;
  const button = form.querySelector("button");
  button.disabled = true;
  try {
    const response = await fetch(path, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(Object.fromEntries(new FormData(form)))
    });
    if (!response.ok) throw new Error(await readError(response));

    const item = await response.json();
    const optionText = path === "/api/products"
      ? `${item.name} · ${item.sku}`
      : `${item.name} · №${item.id}`;
    select.add(new Option(optionText, item.id, true, true));
    form.reset();
    setMessage(`${label} создан и выбран.`);
    if (pairIsValid()) await refreshStock();
  } catch (error) {
    setMessage(error.message || `Не удалось создать ${label.toLowerCase()}.`, true);
  } finally {
    button.disabled = false;
  }
}

async function refreshStock(operationMessage, operationFailed = false) {
  const request = ++stockRequest;
  if (!pairIsValid()) {
    showStock();
    setMessage("Выберите товар и склад.", true);
    return;
  }

  const query = new URLSearchParams({ productId: productSelect.value, warehouseId: warehouseSelect.value });
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
    setMessage("Сначала выберите товар и склад.", true);
    return;
  }

  ++stockRequest;
  const controls = [productSelect, warehouseSelect, refreshButton, ...actionButtons];
  controls.forEach(control => control.disabled = true);
  try {
    let operationMessage = successText;
    let operationFailed = false;
    try {
      const response = await fetch(path, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ productId: Number(productSelect.value), warehouseId: Number(warehouseSelect.value), ...body })
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

document.querySelector("#product-form").addEventListener("submit", event => {
  event.preventDefault();
  createCatalogItem(event.currentTarget, "/api/products", productSelect, "Товар");
});

document.querySelector("#warehouse-form").addEventListener("submit", event => {
  event.preventDefault();
  createCatalogItem(event.currentTarget, "/api/warehouses", warehouseSelect, "Склад");
});

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

for (const select of [productSelect, warehouseSelect]) {
  select.addEventListener("change", () => {
    ++stockRequest;
    showStock();
    if (pairIsValid()) refreshStock();
    else setMessage("Выберите товар и склад.");
  });
}

loadCatalogs();
