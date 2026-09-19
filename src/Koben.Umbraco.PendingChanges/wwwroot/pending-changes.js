import { UmbControllerBase } from "@umbraco-cms/backoffice/class-api";
import { UMB_DOCUMENT_WORKSPACE_CONTEXT } from "@umbraco-cms/backoffice/document";
import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { umbHttpClient } from "@umbraco-cms/backoffice/http-client";
import { encodeFolderName } from "@umbraco-cms/backoffice/router";
import { html, css, nothing } from "@umbraco-cms/backoffice/external/lit";

const API_BASE = "/umbraco/management/api/v1/pending-changes";
const API_SECURITY = [{ type: "http", scheme: "bearer" }];

const PROPERTY_ELEMENT = "umb-content-workspace-property";
const TAB_ELEMENT = "uui-tab";
const TAB_MARK_PREFIX = "content-tab:tab/";
const FLAG_ELEMENT = "koben-pending-change-flag";
const TAB_BADGE_ELEMENT = "umb-badge";
const MARKER_ATTRIBUTE = "data-koben-pending-change";
const TAB_MARKER_ATTRIBUTE = "data-koben-pending-tab";
const PENDING_STATE = "PendingChanges";
const REFRESH_DELAY = 150;
const SCAN_DELAY = 100;

const ACCENT = "inset 3px 0 0 0 var(--uui-color-warning-emphasis, #af7c12)";
const TINT = "color-mix(in srgb, var(--uui-color-warning, #ffd621) 10%, transparent)";

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;
const WEEK = 7 * DAY;

/**
 * The tag under a property's label: this value is saved, it is not live, and this is who last
 * changed it.
 */
export class KobenPendingChangeFlagElement extends UmbLitElement {
  static properties = {
    changedBy: { type: String, attribute: false },
    changedAt: { type: String, attribute: false }
  };

  static styles = css`
    :host {
      display: block;
      margin-top: var(--uui-size-space-2);
    }

    uui-tag {
      --uui-tag-font-size: 11px;
      white-space: normal;
      text-align: left;
    }

    .editor {
      font-weight: 700;
    }
  `;

  render() {
    return html`
      <uui-tag look="secondary" color="warning" title=${this.#absolute()}>
        <uui-icon name="icon-edit"></uui-icon>
        Not published${this.changedBy ? html` &middot; <span class="editor">${this.changedBy}</span>` : nothing}${this.#relative()
          ? html` &middot; ${this.#relative()}`
          : nothing}
      </uui-tag>
    `;
  }

  /** The exact moment of the change, for the tooltip. */
  #absolute() {
    const changed = this.#parse();
    return changed ? changed.toLocaleString() : "";
  }

  /** How long ago the change was made, in the shortest words that stay accurate. */
  #relative() {
    const changed = this.#parse();
    if (!changed) {
      return "";
    }

    const elapsed = Date.now() - changed.getTime();
    if (elapsed < MINUTE) {
      return "just now";
    }
    if (elapsed < HOUR) {
      return `${Math.floor(elapsed / MINUTE)}m ago`;
    }
    if (elapsed < DAY) {
      return `${Math.floor(elapsed / HOUR)}h ago`;
    }
    if (elapsed < WEEK) {
      return `${Math.floor(elapsed / DAY)}d ago`;
    }

    return changed.toLocaleDateString();
  }

  /** The change timestamp as a date, or null when the server sent something unusable. */
  #parse() {
    if (!this.changedAt) {
      return null;
    }

    const parsed = new Date(this.changedAt);
    return Number.isNaN(parsed.getTime()) ? null : parsed;
  }
}

if (!customElements.get(FLAG_ELEMENT)) {
  customElements.define(FLAG_ELEMENT, KobenPendingChangeFlagElement);
}

/**
 * Asks the server which of a document's saved values are still waiting to be published.
 * @param {string} documentId The document's key.
 * @returns {Promise<object | null>} The document's pending state, or null when it cannot be read.
 */
async function fetchPendingChanges(documentId) {
  const result = await umbHttpClient.get({
    url: `${API_BASE}/document/${documentId}`,
    security: [...API_SECURITY]
  });

  if (!result.response?.ok) {
    return null;
  }

  return result.data ?? null;
}

/**
 * Marks the properties of the open document whose saved value has not been published yet, and
 * names the editor who last changed each one.
 *
 * Umbraco has no extension point for decorating a single property, so the flags are attached to
 * the rendered property elements. Everything this adds is tagged, re-applied when the workspace
 * re-renders, and removed again when the context is destroyed.
 */
export class KobenPendingChangesWorkspaceContext extends UmbControllerBase {
  #workspace;
  #changes = [];
  #decorated = new Set();
  #decoratedTabs = new Set();
  #observedRoots = new WeakSet();
  #mutationObserver;
  #refreshTimer;
  #scanTimer;
  #isDestroyed = false;

  constructor(host) {
    super(host);

    this.#mutationObserver = new MutationObserver(() => this.#scheduleScan());

    this.consumeContext(UMB_DOCUMENT_WORKSPACE_CONTEXT, (context) => {
      this.#workspace = context;

      if (!context) {
        this.#setChanges([]);
        return;
      }

      this.observe(context.unique, () => this.#scheduleRefresh(), "kobenPendingChangesUnique");
      this.observe(context.persistedData, () => this.#scheduleRefresh(), "kobenPendingChangesPersisted");
    });
  }

  /** Coalesces the refresh triggers a single save fires. */
  #scheduleRefresh() {
    clearTimeout(this.#refreshTimer);
    this.#refreshTimer = setTimeout(() => this.#refresh(), REFRESH_DELAY);
  }

  /** Asks the server what is unpublished on the document the workspace currently holds. */
  async #refresh() {
    const unique = this.#workspace?.getUnique?.();
    if (!unique) {
      this.#setChanges([]);
      return;
    }

    const pendingChanges = await fetchPendingChanges(unique);
    if (this.#isDestroyed) {
      return;
    }

    this.#setChanges(pendingChanges?.state === PENDING_STATE ? (pendingChanges.properties ?? []) : []);
  }

  /** Takes a new set of unpublished values and re-marks the workspace against it. */
  #setChanges(changes) {
    this.#changes = changes;
    this.#scheduleScan();
  }

  /** Coalesces the re-render storms a workspace produces while it loads or switches tab. */
  #scheduleScan() {
    clearTimeout(this.#scanTimer);
    this.#scanTimer = setTimeout(() => this.#scan(), SCAN_DELAY);
  }

  /** Walks the open workspace and brings every property's flag in line with the current data. */
  #scan() {
    if (this.#isDestroyed) {
      return;
    }

    const host = this.getHostElement();
    if (!host) {
      return;
    }

    const properties = [];
    const tabs = [];
    this.#collectProperties(host, properties, tabs);

    // A workspace context is not guaranteed to be hosted above the editor it belongs to. If the
    // host holds no properties, fall back to the open backoffice so the flags still land.
    if (properties.length === 0 && this.#changes.length > 0) {
      this.#collectProperties(document.body, properties, tabs);
    }

    const seen = new Set(properties);
    for (const stale of [...this.#decorated]) {
      if (!seen.has(stale) || !stale.isConnected) {
        this.#clear(stale);
      }
    }

    for (const property of properties) {
      const change = this.#findChange(property);
      if (change) {
        this.#decorate(property, change);
      } else {
        this.#clear(property);
      }
    }

    this.#markTabs(tabs);
  }

  /**
   * Finds the workspace's property elements, crossing shadow roots but never descending into a
   * property editor's own markup, and watches every root it passes through for re-renders.
   * @param {Document|ShadowRoot|Element} root The root to search.
   * @param {Array<Element>} found Collects the property elements.
   * @param {Array<Element>} tabs Collects the content tabs found on the way.
   */
  #collectProperties(root, found, tabs) {
    this.#observeRoot(root);

    if (root.shadowRoot) {
      this.#collectProperties(root.shadowRoot, found, tabs);
    }

    for (const element of root.querySelectorAll("*")) {
      if (element.localName === PROPERTY_ELEMENT) {
        found.push(element);
        continue;
      }

      if (element.localName === TAB_ELEMENT && element.getAttribute("data-mark")?.startsWith(TAB_MARK_PREFIX)) {
        tabs.push(element);
      }

      if (element.shadowRoot) {
        this.#collectProperties(element.shadowRoot, found, tabs);
      }
    }
  }

  /**
   * Watches a shadow root once, so properties appearing later (another tab, a lazy group) get flagged too.
   * @param {Document|ShadowRoot|Element} root The root to watch.
   */
  #observeRoot(root) {
    if (this.#observedRoots.has(root)) {
      return;
    }

    this.#observedRoots.add(root);
    this.#mutationObserver.observe(root, { childList: true, subtree: true });
  }

  /**
   * Picks the unpublished change a rendered property is showing. An invariant value shown inside a
   * variant document carries no culture of its own, so a change without a culture matches any pane.
   * @param {Element} property The rendered property element.
   * @returns {object | undefined} The change to flag, or undefined when the property is up to date.
   */
  #findChange(property) {
    const alias = property.getAttribute("alias");
    if (!alias) {
      return undefined;
    }

    const variantId = property._datasetVariantId;
    const culture = variantId?.culture ?? null;
    const segment = variantId?.segment ?? null;

    return this.#changes
      .filter(
        (change) =>
          change.alias === alias &&
          (!change.culture || change.culture === culture) &&
          (!change.segment || change.segment === segment)
      )
      .sort((left, right) => new Date(right.changedAt) - new Date(left.changedAt))[0];
  }

  /**
   * Puts a dot on every tab holding unpublished values, so an editor can see there is something to
   * publish on a tab they are not looking at.
   * @param {Array<Element>} tabs The content tabs currently rendered.
   */
  #markTabs(tabs) {
    const counts = new Map();
    for (const change of this.#changes) {
      if (!change.tab) {
        continue;
      }

      const slug = encodeFolderName(change.tab);
      counts.set(slug, (counts.get(slug) ?? 0) + 1);
    }

    for (const tab of tabs) {
      const slug = (tab.getAttribute("data-mark") ?? "").slice(TAB_MARK_PREFIX.length);
      const count = counts.get(slug) ?? 0;

      if (count > 0) {
        this.#markTab(tab, count);
      } else {
        this.#clearTab(tab);
      }
    }

    for (const stale of [...this.#decoratedTabs]) {
      if (!tabs.includes(stale) || !stale.isConnected) {
        this.#clearTab(stale);
      }
    }
  }

  /**
   * Adds the dot to one tab, reusing Umbraco's own badge so it sits where a tab's badges belong.
   * @param {Element} tab The tab element.
   * @param {number} count How many of the tab's values are unpublished.
   */
  #markTab(tab, count) {
    const label = count === 1 ? "1 unpublished change" : `${count} unpublished changes`;
    if (tab.getAttribute(TAB_MARKER_ATTRIBUTE) === label && tab.__kobenDot?.isConnected) {
      return;
    }

    tab.setAttribute(TAB_MARKER_ATTRIBUTE, label);

    let dot = tab.__kobenDot;
    if (!dot?.isConnected) {
      dot = document.createElement(TAB_BADGE_ELEMENT);
      dot.setAttribute("slot", "extra");
      dot.setAttribute("color", "warning");
      tab.appendChild(dot);
    }

    dot.setAttribute("title", label);
    tab.__kobenDot = dot;
    this.#decoratedTabs.add(tab);
  }

  /**
   * Takes the dot back off a tab.
   * @param {Element} tab The tab element.
   */
  #clearTab(tab) {
    if (tab.hasAttribute(TAB_MARKER_ATTRIBUTE)) {
      tab.removeAttribute(TAB_MARKER_ATTRIBUTE);
    }

    tab.__kobenDot?.remove();
    tab.__kobenDot = undefined;
    this.#decoratedTabs.delete(tab);
  }

  /**
   * Flags one property: an accent down its edge and a tag under its label.
   * @param {Element} property The rendered property element.
   * @param {object} change The unpublished change it is showing.
   */
  async #decorate(property, change) {
    const signature = `${change.culture ?? ""}|${change.segment ?? ""}|${change.changedBy}|${change.changedAt}`;
    const isIntact = property.__kobenFlag?.isConnected && property.__kobenLayout?.isConnected;
    if (property.getAttribute(MARKER_ATTRIBUTE) === signature && isIntact) {
      return;
    }

    property.setAttribute(MARKER_ATTRIBUTE, signature);
    this.#decorated.add(property);

    const layout = await this.#resolveLayout(property);
    if (!layout || this.#isDestroyed || !property.isConnected) {
      return;
    }

    // The property element itself is laid out inline, so the highlight goes on the layout inside it,
    // which is the grid holding both the label and the editor.
    layout.style.setProperty("box-shadow", ACCENT);
    layout.style.setProperty("background-color", TINT);
    layout.style.setProperty("padding-left", "var(--uui-size-space-4)");

    let flag = layout.querySelector(FLAG_ELEMENT);
    if (!flag) {
      flag = document.createElement(FLAG_ELEMENT);
      flag.setAttribute("slot", "description");
      layout.appendChild(flag);
    }

    flag.changedBy = change.changedBy;
    flag.changedAt = change.changedAt;
    property.__kobenFlag = flag;
    property.__kobenLayout = layout;
  }

  /**
   * Waits for a property to finish rendering and returns the layout its label lives in.
   * @param {Element} property The rendered property element.
   * @returns {Promise<Element | null>} The property layout, or null when the property never rendered one.
   */
  async #resolveLayout(property) {
    const typeBased = await this.#renderedChild(property, "umb-property-type-based-property");
    const inner = await this.#renderedChild(typeBased, "umb-property");

    return this.#renderedChild(inner, "umb-property-layout");
  }

  /**
   * Waits for an element's own render, then reaches into its shadow root for a child element.
   * @param {Element | null} element The element to look inside.
   * @param {string} selector The child element to find.
   * @returns {Promise<Element | null>} The child, or null when it is not there.
   */
  async #renderedChild(element, selector) {
    if (!element) {
      return null;
    }

    await element.updateComplete?.catch?.(() => undefined);

    return element.shadowRoot?.querySelector(selector) ?? null;
  }

  /**
   * Returns a property to the way Umbraco drew it.
   * @param {Element} property The property element to clean up.
   */
  #clear(property) {
    if (!property.hasAttribute(MARKER_ATTRIBUTE)) {
      this.#decorated.delete(property);
      return;
    }

    property.removeAttribute(MARKER_ATTRIBUTE);
    property.__kobenLayout?.style.removeProperty("box-shadow");
    property.__kobenLayout?.style.removeProperty("background-color");
    property.__kobenLayout?.style.removeProperty("padding-left");
    property.__kobenLayout = undefined;
    property.__kobenFlag?.remove();
    property.__kobenFlag = undefined;
    this.#decorated.delete(property);
  }

  destroy() {
    this.#isDestroyed = true;
    clearTimeout(this.#refreshTimer);
    clearTimeout(this.#scanTimer);
    this.#mutationObserver.disconnect();

    for (const property of [...this.#decorated]) {
      this.#clear(property);
    }

    for (const tab of [...this.#decoratedTabs]) {
      this.#clearTab(tab);
    }

    super.destroy();
  }
}

export { KobenPendingChangesWorkspaceContext as api };
export default KobenPendingChangesWorkspaceContext;
