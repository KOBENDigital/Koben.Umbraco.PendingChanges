import { UmbControllerBase } from "@umbraco-cms/backoffice/class-api";
import { UmbContextToken } from "@umbraco-cms/backoffice/context-api";
import { UMB_DOCUMENT_WORKSPACE_CONTEXT } from "@umbraco-cms/backoffice/document";
import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { UmbArrayState } from "@umbraco-cms/backoffice/observable-api";
import { umbHttpClient } from "@umbraco-cms/backoffice/http-client";
import { encodeFolderName } from "@umbraco-cms/backoffice/router";
import { html, css, nothing, unsafeCSS } from "@umbraco-cms/backoffice/external/lit";

const API_BASE = "/umbraco/management/api/v1/pending-changes";
const API_SECURITY = [{ type: "http", scheme: "bearer" }];

/** The document's own properties, as the content editor draws them. */
const PROPERTY_ELEMENT = "umb-content-workspace-property";

/** A block's properties, as a block workspace — a modal or an inline block — draws them. */
const BLOCK_PROPERTY_ELEMENT = "umb-block-workspace-view-edit-property";

/** The cards a block editor draws for the blocks it holds, whichever editor drew them. */
const BLOCK_ENTRY_ELEMENTS = new Set([
  "umb-block-list-entry",
  "umb-block-grid-entry",
  "umb-block-single-entry",
  "umb-rte-block",
  "umb-rte-block-inline"
]);

const TAB_ELEMENT = "uui-tab";
const TAB_MARK_PREFIX = "content-tab:tab/";
const FLAG_ELEMENT = "koben-pending-change-flag";
const BLOCK_MARK_ELEMENT = "koben-pending-block-mark";
const TREE_SIGN_ELEMENT = "koben-pending-tree-sign";
const TAB_BADGE_ELEMENT = "umb-badge";
const TREE_ITEM_ELEMENT = "umb-document-tree-item";
const MARKER_ATTRIBUTE = "data-koben-pending-change";
const HIGHLIGHT_ATTRIBUTE = "data-koben-pending-highlight";
const TREE_MARKER_ATTRIBUTE = "data-koben-pending-tree";
const INVALID_ATTRIBUTE = "invalid";
const BLOCK_MARKER_ATTRIBUTE = "data-koben-pending-block";
const TAB_MARKER_ATTRIBUTE = "data-koben-pending-tab";
const PENDING_STATE = "PendingChanges";
const BLOCK_ADDED = "Added";
const BLOCK_REMOVED = "Removed";
const SETTINGS_SCOPE = "Settings";
const REFRESH_DELAY = 150;
const SCAN_DELAY = 100;

/** Where the pending dot moves to when Umbraco's own badge — a validation "!" — holds the corner. */
const CROWDED_TAB_INSET = "0 20px auto auto";

/** The block card's own buttons, each of which carries its own "!" when the block is invalid. */
const BLOCK_ACTION_LIST_ELEMENT = "umb-block-action-list";
const BLOCK_ACTION_ELEMENT = "umb-block-action";

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;
const WEEK = 7 * DAY;

/**
 * Adopted into every property layout the workspace draws. The layout keeps its own `invalid`
 * attribute up to date as validation runs, so a stylesheet keyed on it follows validation with
 * nothing to observe. A property that fails validation is the one an editor has to act on, so its
 * red wins over the amber of a pending change when a property is both.
 *
 * It is always red. Umbraco repaints `--uui-color-invalid` in its warning yellow after a plain save —
 * the draft is kept, it just cannot be published — and every value a save has just kept is also a
 * pending change, so a highlight following that token would look exactly like the pending one.
 *
 * The invalid band is drawn with shadows that reach out into the gutter rather than with padding,
 * so a value that flips between valid and invalid as it is typed does not move under the cursor.
 * The tint is mixed into the surface rather than made transparent because the shadows overlap.
 */
const LAYOUT_STYLES = css`
  :host([${unsafeCSS(HIGHLIGHT_ATTRIBUTE)}]) {
    box-shadow: inset 3px 0 0 0 var(--uui-color-warning-emphasis, #af7c12);
    background-color: color-mix(in srgb, var(--uui-color-warning, #ffd621) 10%, transparent);
    padding-left: var(--uui-size-space-4);
  }

  :host([${unsafeCSS(INVALID_ATTRIBUTE)}]) {
    --koben-invalid-tint: color-mix(in srgb, var(--uui-color-danger, #df2a5d) 8%, var(--uui-color-surface, #fff));
    background-color: var(--koben-invalid-tint);
    box-shadow:
      calc(var(--uui-size-space-4) * -1) 0 0 0 var(--koben-invalid-tint),
      var(--uui-size-space-4) 0 0 0 var(--koben-invalid-tint),
      calc(var(--uui-size-space-4) * -1 - 4px) 0 0 0 var(--uui-color-danger, #df2a5d);
  }

  :host([${unsafeCSS(INVALID_ATTRIBUTE)}]) #label {
    color: var(--uui-color-danger-standalone, #ae1e47);
  }

  #invalid-badge uui-badge {
    min-width: 16px;
    min-height: 16px;
    padding: 0 4px;
    font-size: 10px;
  }
`;

/**
 * Umbraco's "!" badges are 24px, which is loud next to a 14px label and bigger than the tab text
 * they sit on. Adopted into whatever shadow root draws one — a tab's badge, a block card, a card's
 * buttons — and styling the badge from outside, which wins over the badge's own `:host` sizes.
 */
const BADGE_STYLES = css`
  uui-badge {
    min-width: 16px;
    min-height: 16px;
    padding: 0 4px;
    font-size: 10px;
  }
`;

/** The pending dot on a tab: it has no text, so it is a dot rather than a badge. */
const DOT_STYLES = css`
  uui-badge {
    min-width: 10px;
    min-height: 10px;
    padding: 0;
  }
`;

/**
 * Adopted into every block card the workspace finds. Umbraco already reflects a block's validation
 * onto its card, but only as a hairline and a small badge; this makes an invalid block as easy to
 * spot in a long list as an invalid field is, in the same red. It is an outline so it sits outside
 * the pending mark rather than under it.
 */
const ENTRY_STYLES = css`
  :host([content-invalid]),
  :host([settings-invalid]),
  :host([location-invalid]) {
    outline: 2px solid var(--uui-color-danger, #df2a5d);
    outline-offset: 1px;
    border-radius: var(--uui-border-radius, 3px);
  }

  uui-badge {
    min-width: 16px;
    min-height: 16px;
    padding: 0 4px;
    font-size: 10px;
  }
`;

/**
 * Adopted into a document tree item while it carries the pending changes sign: an amber tile behind
 * the page's icon. The tile is a background plus a spread shadow of the same colour, so it grows
 * around the icon without moving the icon or the name.
 */
const TREE_ROW_STYLES = css`
  :host([${unsafeCSS(TREE_MARKER_ATTRIBUTE)}]) #icon-container {
    --koben-tree-tile: color-mix(in srgb, var(--uui-color-warning, #fad634) 70%, transparent);
    border-radius: var(--uui-border-radius, 3px);
    background-color: var(--koben-tree-tile);
    box-shadow: 0 0 0 3px var(--koben-tree-tile);
  }
`;

/**
 * Adds a stylesheet to a shadow root once. Lit builds the sheet only where the browser can adopt
 * one; anywhere else this does nothing, which leaves the element as Umbraco drew it.
 * @param {ShadowRoot | null | undefined} root The shadow root to style.
 * @param {import("@umbraco-cms/backoffice/external/lit").CSSResult} styles The styles to adopt.
 */
function adoptStyles(root, styles) {
  const sheet = styles.styleSheet;
  if (!root || !sheet || root.adoptedStyleSheets.includes(sheet)) {
    return;
  }

  root.adoptedStyleSheets = [...root.adoptedStyleSheets, sheet];
}

/**
 * Takes a stylesheet back out of a shadow root.
 * @param {ShadowRoot | null | undefined} root The shadow root it was adopted into.
 * @param {import("@umbraco-cms/backoffice/external/lit").CSSResult} styles The styles to remove.
 */
function dropStyles(root, styles) {
  const sheet = styles.styleSheet;
  if (!root || !sheet || !root.adoptedStyleSheets.includes(sheet)) {
    return;
  }

  root.adoptedStyleSheets = root.adoptedStyleSheets.filter((adopted) => adopted !== sheet);
}

/**
 * Shares one document's unpublished blocks with the block workspaces opened from it. A block's
 * editor is a workspace of its own — in a modal, or expanded inline — so it cannot see the
 * document's answer without being handed it.
 */
export const KOBEN_PENDING_CHANGES_CONTEXT = new UmbContextToken("Koben.PendingChanges");

/**
 * The change timestamp as a date, or null when the server sent something unusable.
 * @param {string | undefined} value The timestamp.
 * @returns {Date | null} The parsed date.
 */
function parseDate(value) {
  if (!value) {
    return null;
  }

  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
}

/**
 * How long ago a change was made, in the shortest words that stay accurate.
 * @param {string | undefined} value The timestamp.
 * @returns {string} The elapsed time, or an empty string when the timestamp is unusable.
 */
function formatElapsed(value) {
  const changed = parseDate(value);
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

/**
 * Counts things in the words an editor would use.
 * @param {number} count How many.
 * @param {string} noun The singular noun.
 * @returns {string} The counted phrase.
 */
function plural(count, noun) {
  return count === 1 ? `1 ${noun}` : `${count} ${noun}s`;
}

/**
 * The tag under a property's label: this value is saved, it is not live, and this is who last
 * changed it.
 */
export class KobenPendingChangeFlagElement extends UmbLitElement {
  static properties = {
    changedBy: { type: String, attribute: false },
    changedAt: { type: String, attribute: false },
    detail: { type: String, attribute: false }
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
    const elapsed = formatElapsed(this.changedAt);

    return html`
      <uui-tag look="secondary" color="warning" title=${this.#title()}>
        <uui-icon name="icon-edit"></uui-icon>
        Not published${this.changedBy ? html` &middot; <span class="editor">${this.changedBy}</span>` : nothing}${elapsed
          ? html` &middot; ${elapsed}`
          : nothing}${this.detail ? html` &middot; ${this.detail}` : nothing}
      </uui-tag>
    `;
  }

  /** The exact moment of the change, for the tooltip. */
  #title() {
    const changed = parseDate(this.changedAt);
    return changed ? changed.toLocaleString() : "";
  }
}

if (!customElements.get(FLAG_ELEMENT)) {
  customElements.define(FLAG_ELEMENT, KobenPendingChangeFlagElement);
}

/**
 * The mark on a block's card: an amber outline over the block, and a tag naming what is waiting to
 * be published inside it. It sits inside the card's own shadow root because a block card has no
 * slot of its own to render into, it never takes a click, and it is removed again with the rest of
 * the decoration.
 */
export class KobenPendingBlockMarkElement extends UmbLitElement {
  static properties = {
    label: { type: String, attribute: false }
  };

  static styles = css`
    :host {
      position: absolute;
      box-sizing: border-box;
      inset: 1px;
      z-index: 2;
      pointer-events: none;
      border: 2px solid var(--uui-color-warning-emphasis, #af7c12);
      border-radius: var(--uui-border-radius, 3px);
    }

    /*
     * A block's own actions sit in the same corner — on a one-line card, in the same place — and
     * they must win. Every block editor's card publishes the opacity it is fading those actions
     * to, so the tag takes the opposite of it: it is there while the card is at rest and gone the
     * moment the actions appear. What it said is on the card's own tooltip by then.
     */
    uui-tag {
      position: absolute;
      right: var(--uui-size-space-2, 6px);
      bottom: var(--uui-size-space-2, 6px);
      --uui-tag-font-size: 10px;
      transition: opacity 120ms;
      opacity: calc(
        1 -
          max(
            var(--umb-block-list-entry-actions-opacity, 0),
            var(--umb-block-grid-entry-actions-opacity, 0),
            var(--umb-block-single-entry-actions-opacity, 0),
            var(--umb-block-entry-actions-opacity, 0)
          )
      );
    }
  `;

  render() {
    return html`
      <uui-tag look="secondary" color="warning">
        <uui-icon name="icon-edit"></uui-icon>
        ${this.label}
      </uui-tag>
    `;
  }
}

if (!customElements.get(BLOCK_MARK_ELEMENT)) {
  customElements.define(BLOCK_MARK_ELEMENT, KobenPendingBlockMarkElement);
}

/** How many of this package's signs each tree item is currently showing — its preview and its popover. */
const treeRowHolds = new WeakMap();

/**
 * The document tree item a sign was rendered for. A sign is drawn inside Umbraco's sign bundle,
 * and the bundle inside the tree item, so it is two shadow roots up. Anywhere else a sign is shown —
 * a picker, a collection — this finds nothing and the sign is only a sign.
 * @param {Element} sign The sign element.
 * @returns {Element | null} The tree item, or null when the sign is not in the document tree.
 */
function treeItemOf(sign) {
  const bundle = sign.getRootNode()?.host;
  const item = bundle?.getRootNode()?.host;
  return item?.localName === TREE_ITEM_ELEMENT ? item : null;
}

/**
 * The sign Umbraco draws on a document with unpublished changes, drawn in amber rather than grey,
 * with an amber tile behind the page's icon so the page stands out in a long tree.
 *
 * Umbraco decides when a document has unpublished changes and only renders a sign when it does, so
 * the highlight is exactly as current as the tree: it goes on when the sign is connected and comes
 * off when the sign is. That is Umbraco's own flag, not this package's comparison — the tree would
 * otherwise need a request per row.
 */
export class KobenPendingTreeSignElement extends UmbLitElement {
  #item = null;

  static styles = css`
    umb-icon {
      color: var(--uui-color-warning-standalone, #a17700);
      filter: drop-shadow(-1px 0 0 var(--umb-sign-bundle-bg)) drop-shadow(0 -1px 0 var(--umb-sign-bundle-bg))
        drop-shadow(0 1px 0 var(--umb-sign-bundle-bg));
    }

    umb-icon::before {
      content: "";
      position: absolute;
      z-index: -1;
      border-radius: 50%;
      inset: 2px;
      background-color: var(--umb-sign-bundle-bg);
    }
  `;

  connectedCallback() {
    super.connectedCallback();

    this.#item = treeItemOf(this);
    const root = this.#item?.shadowRoot;
    if (!root) {
      return;
    }

    const holds = treeRowHolds.get(this.#item) ?? 0;
    treeRowHolds.set(this.#item, holds + 1);

    if (holds === 0) {
      adoptStyles(root, TREE_ROW_STYLES);
      this.#item.setAttribute(TREE_MARKER_ATTRIBUTE, "");
    }
  }

  disconnectedCallback() {
    super.disconnectedCallback();

    const item = this.#item;
    this.#item = null;
    if (!item || !treeRowHolds.has(item)) {
      return;
    }

    const holds = treeRowHolds.get(item) - 1;
    if (holds > 0) {
      treeRowHolds.set(item, holds);
      return;
    }

    treeRowHolds.delete(item);
    item.removeAttribute(TREE_MARKER_ATTRIBUTE);
    dropStyles(item.shadowRoot, TREE_ROW_STYLES);
  }

  render() {
    return html`<umb-icon name="icon-edit"></umb-icon>`;
  }
}

if (!customElements.get(TREE_SIGN_ELEMENT)) {
  customElements.define(TREE_SIGN_ELEMENT, KobenPendingTreeSignElement);
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
 * Marks what the open workspace is holding but has not published: the document's own properties,
 * the blocks inside them, and — once a block is opened — the block's own properties.
 *
 * One class serves two workspaces, because it is one file and the manifest registers it twice. On
 * the document workspace it fetches the answer, flags the document's properties and marks the block
 * cards; on a block workspace it takes the same answer through {@link KOBEN_PENDING_CHANGES_CONTEXT}
 * and flags the block's own properties.
 *
 * Umbraco has no extension point for decorating a single property, so the flags are attached to the
 * rendered elements. Everything this adds is tagged, re-applied when the workspace re-renders, and
 * removed again when the context is destroyed.
 */
export class KobenPendingChangesWorkspaceContext extends UmbControllerBase {
  #workspace;
  #isBlockWorkspace;
  #changes = [];
  #blocksByKey = new Map();
  #blocksByOwner = new Map();
  #blockState = new UmbArrayState([], (block) => block.key);
  #decorated = new Set();
  #decoratedEntries = new Set();
  #decoratedTabs = new Set();
  #layouts = new WeakMap();
  #styledLayouts = new Set();
  #styledEntries = new Set();
  #styledBadgeRoots = new Set();
  #observedRoots = new WeakSet();
  #mutationObserver;
  #refreshTimer;
  #scanTimer;
  #isDestroyed = false;

  /** Every block of the open document that is waiting to be published, for the block workspaces. */
  blocks = this.#blockState.asObservable();

  /**
   * @param {object} host The controller host the extension was created against.
   * @param {object} [workspace] The workspace context the extension was registered for. Umbraco
   *   creates a workspace context extension as `new Api(host, workspaceContext)`, and which of the
   *   two workspaces it is decides everything this class does.
   */
  constructor(host, workspace) {
    super(host);

    this.#workspace = workspace;
    this.#isBlockWorkspace = workspace?.IS_BLOCK_WORKSPACE_CONTEXT === true;
    this.#mutationObserver = new MutationObserver(() => this.#scheduleScan());

    if (this.#isBlockWorkspace) {
      this.#consumeDocumentAnswer();
      return;
    }

    this.provideContext(KOBEN_PENDING_CHANGES_CONTEXT, this);

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

  /** Follows the document's answer from inside a block, and re-marks whenever the block changes. */
  #consumeDocumentAnswer() {
    this.consumeContext(KOBEN_PENDING_CHANGES_CONTEXT, (context) => {
      this.observe(
        context?.blocks,
        (blocks) => this.#indexBlocks(blocks ?? []),
        "kobenPendingChangesBlocks"
      );
    });

    this.observe(this.#workspace?.contentKey, () => this.#scheduleScan(), "kobenPendingChangesBlockKey");
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

    const blocks = changes.flatMap((change) => change.blocks ?? []);
    this.#blockState.setValue(blocks);
    this.#indexBlocks(blocks);
  }

  /**
   * Indexes the unpublished blocks twice: by the element that changed, which is what a block's own
   * editor knows itself by, and by the block that element belongs to, which is what a card is.
   * @param {Array<object>} blocks The blocks waiting to be published.
   */
  #indexBlocks(blocks) {
    this.#blocksByKey = new Map(blocks.map((block) => [block.key, block]));
    this.#blocksByOwner = new Map();

    for (const block of blocks) {
      // A removed block is gone from the editor, so there is no card left to mark. It is still
      // part of what publishing would do, and the property flag above it says so.
      if (block.status === BLOCK_REMOVED) {
        continue;
      }

      const marked = this.#blocksByOwner.get(block.ownerKey);
      if (marked) {
        marked.push(block);
      } else {
        this.#blocksByOwner.set(block.ownerKey, [block]);
      }
    }

    this.#scheduleScan();
  }

  /** Coalesces the re-render storms a workspace produces while it loads or switches tab. */
  #scheduleScan() {
    clearTimeout(this.#scanTimer);
    this.#scanTimer = setTimeout(() => this.#scan(), SCAN_DELAY);
  }

  /** Walks the open workspace and brings every mark in line with the current data. */
  #scan() {
    if (this.#isDestroyed) {
      return;
    }

    const host = this.getHostElement();
    if (!host) {
      return;
    }

    const found = { properties: [], tabs: [], entries: [] };
    this.#collect(host, found, false);

    // A workspace context is not guaranteed to be hosted above the editor it belongs to. If the
    // host holds no properties, fall back to the open backoffice so the flags still land.
    if (!this.#isBlockWorkspace && found.properties.length === 0 && this.#changes.length > 0) {
      this.#collect(document.body, found, false);
    }

    this.#styleLayouts(found.properties);
    this.#styleEntries(found.entries);
    this.#markProperties(found.properties);
    this.#markEntries(found.entries);
    this.#markTabs(found.tabs);
  }

  /**
   * Finds everything the walk can decorate, crossing shadow roots. A property element is only the
   * document's own when nothing above it was a property: the walk carries on through it to reach
   * the blocks inside, and a block's properties are matched by the block they belong to rather
   * than by where they were found.
   * @param {Document|ShadowRoot|Element} root The root to search.
   * @param {{properties: Array<Element>, tabs: Array<Element>, entries: Array<Element>}} found Collects what is there.
   * @param {boolean} insideProperty Whether the walk has already passed through a property.
   */
  #collect(root, found, insideProperty) {
    this.#observeRoot(root, insideProperty);

    if (root.shadowRoot) {
      this.#collect(root.shadowRoot, found, insideProperty);
    }

    for (const element of root.querySelectorAll("*")) {
      const name = element.localName;

      if (name === PROPERTY_ELEMENT) {
        if (!insideProperty && !this.#isBlockWorkspace) {
          found.properties.push(element);
        }

        // The document's blocks are drawn inside its properties, so the walk only goes in when
        // there are blocks to look for: unpublished ones anywhere, or invalid ones in this property.
        if (this.#tracksBlocks() || this.#isInvalid(element)) {
          this.#collect(element, found, true);
        }

        continue;
      }

      if (name === BLOCK_PROPERTY_ELEMENT) {
        if (this.#isBlockWorkspace) {
          found.properties.push(element);
        }
      } else if (BLOCK_ENTRY_ELEMENTS.has(name)) {
        found.entries.push(element);
      } else if (
        name === TAB_ELEMENT &&
        !insideProperty &&
        element.getAttribute("data-mark")?.startsWith(TAB_MARK_PREFIX)
      ) {
        found.tabs.push(element);
      }

      if (element.shadowRoot) {
        this.#collect(element.shadowRoot, found, insideProperty);
      }
    }
  }

  /** Whether this workspace has any unpublished blocks to look for. */
  #tracksBlocks() {
    return this.#blocksByKey.size > 0;
  }

  /**
   * Whether a property is failing validation, as far as its layout has said so yet. A property holding
   * blocks is invalid when any block inside it is.
   * @param {Element} property The rendered property element.
   * @returns {boolean} Whether the property's layout is marked invalid.
   */
  #isInvalid(property) {
    return this.#layouts.get(property)?.hasAttribute(INVALID_ATTRIBUTE) === true;
  }

  /**
   * Gives every rendered property's layout the stylesheet that draws both highlights, and watches
   * the layout's `invalid` attribute so a block editor that turns invalid gets walked into.
   * @param {Array<Element>} properties The property elements currently rendered.
   */
  #styleLayouts(properties) {
    for (const stale of [...this.#styledLayouts]) {
      if (!stale.isConnected) {
        this.#styledLayouts.delete(stale);
      }
    }

    for (const property of properties) {
      this.#styleLayout(property);
    }
  }

  /**
   * Styles one property's layout, once it has rendered.
   * @param {Element} property The rendered property element.
   */
  async #styleLayout(property) {
    const known = this.#layouts.get(property);
    const layout = await this.#layoutOf(property);
    if (!layout || this.#isDestroyed) {
      return;
    }

    adoptStyles(layout.shadowRoot, LAYOUT_STYLES);
    this.#styledLayouts.add(layout);

    if (layout !== known) {
      this.#mutationObserver.observe(layout, { attributes: true, attributeFilter: [INVALID_ATTRIBUTE] });

      // A property that was already invalid when it rendered — a tab opened after a failed save —
      // never changes the attribute, so it is walked into now rather than on its next change.
      if (layout.hasAttribute(INVALID_ATTRIBUTE)) {
        this.#scheduleScan();
      }
    }
  }

  /**
   * The layout a property draws its label and editor in, resolved once and remembered.
   * @param {Element} property The rendered property element.
   * @returns {Promise<Element | null>} The layout, or null when the property never rendered one.
   */
  async #layoutOf(property) {
    const known = this.#layouts.get(property);
    if (known?.isConnected) {
      return known;
    }

    const layout = await this.#resolveLayout(property);
    if (layout) {
      this.#layouts.set(property, layout);
    }

    return layout;
  }

  /**
   * Gives every rendered block card the stylesheet that makes an invalid block stand out.
   * @param {Array<Element>} entries The block cards currently rendered.
   */
  #styleEntries(entries) {
    for (const stale of [...this.#styledEntries]) {
      if (!stale.isConnected) {
        this.#styledEntries.delete(stale);
      }
    }

    for (const entry of entries) {
      adoptStyles(entry.shadowRoot, ENTRY_STYLES);
      this.#styledEntries.add(entry);

      const actionList = entry.shadowRoot?.querySelector(BLOCK_ACTION_LIST_ELEMENT);
      for (const action of actionList?.shadowRoot?.querySelectorAll(BLOCK_ACTION_ELEMENT) ?? []) {
        this.#styleBadgeRoot(action.shadowRoot, BADGE_STYLES);
      }
    }
  }

  /**
   * Shrinks the badges drawn in one shadow root, and remembers the root so `destroy()` can put
   * them back.
   * @param {ShadowRoot | null | undefined} root The shadow root holding the badge.
   * @param {import("@umbraco-cms/backoffice/external/lit").CSSResult} styles The size to give it.
   */
  #styleBadgeRoot(root, styles) {
    if (!root) {
      return;
    }

    adoptStyles(root, styles);
    this.#styledBadgeRoots.add(root);
  }

  /**
   * Watches a shadow root once, so properties and blocks appearing later (another tab, a lazy
   * group, a block that was just opened) get marked too.
   *
   * Inside a property only the block editors are watched. A property editor re-renders on every
   * keystroke, and nothing it does to its own markup can change what is waiting to be published.
   * @param {Document|ShadowRoot|Element} root The root to watch.
   * @param {boolean} insideProperty Whether the root sits inside a property.
   */
  #observeRoot(root, insideProperty) {
    if (this.#observedRoots.has(root)) {
      return;
    }

    const owner = root.host ?? root;
    const name = owner.localName ?? "";

    if (insideProperty && name !== PROPERTY_ELEMENT && !name.includes("block")) {
      return;
    }

    this.#observedRoots.add(root);
    this.#mutationObserver.observe(root, { childList: true, subtree: true });
  }

  /**
   * Brings the flag on every rendered property in line with the current data.
   * @param {Array<Element>} properties The property elements currently rendered.
   */
  #markProperties(properties) {
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
  }

  /**
   * Picks the unpublished change a rendered property is showing. An invariant value shown inside a
   * variant document carries no culture of its own, so a change without a culture matches any pane.
   * @param {Element} property The rendered property element.
   * @returns {object | undefined} The change to flag, or undefined when the property is up to date.
   */
  #findChange(property) {
    const changes = this.#isBlockWorkspace ? this.#blockChangesFor(property) : this.#changes;
    const alias = this.#aliasOf(property);
    if (!alias) {
      return undefined;
    }

    const variantId = this.#isBlockWorkspace ? property.variantId : property._datasetVariantId;
    const culture = variantId?.culture ?? null;
    const segment = variantId?.segment ?? null;

    return changes
      .filter(
        (change) =>
          change.alias === alias &&
          (!change.culture || change.culture === culture) &&
          (!change.segment || change.segment === segment)
      )
      .sort((left, right) => new Date(right.changedAt) - new Date(left.changedAt))[0];
  }

  /**
   * Reads the alias a rendered property is drawing, from wherever that workspace keeps it.
   * @param {Element} property The rendered property element.
   * @returns {string | undefined} The property type alias.
   */
  #aliasOf(property) {
    return this.#isBlockWorkspace ? property.property?.alias : (property.getAttribute("alias") ?? undefined);
  }

  /**
   * Finds the unpublished values of the block element a rendered block property belongs to. The
   * element asked is the one the property is bound to, so a block nested inside the open one is
   * matched to itself rather than to its parent.
   * @param {Element} property The rendered block property element.
   * @returns {Array<object>} The block's unpublished properties.
   */
  #blockChangesFor(property) {
    const key = property.ownerContext?.getUnique?.();
    return (key && this.#blocksByKey.get(key)?.properties) || [];
  }

  /**
   * Brings the mark on every rendered block card in line with the current data.
   * @param {Array<Element>} entries The block cards currently rendered.
   */
  #markEntries(entries) {
    const seen = new Set(entries);
    for (const stale of [...this.#decoratedEntries]) {
      if (!seen.has(stale) || !stale.isConnected) {
        this.#clearEntry(stale);
      }
    }

    for (const entry of entries) {
      const blocks = this.#blocksByOwner.get(this.#contentKeyOf(entry));
      if (blocks) {
        this.#markEntry(entry, blocks);
      } else {
        this.#clearEntry(entry);
      }
    }
  }

  /**
   * Reads the block a card is drawing.
   * @param {Element} entry The block card.
   * @returns {string | undefined} The block's content key.
   */
  #contentKeyOf(entry) {
    return entry.contentKey ?? entry.getAttribute("data-content-key") ?? undefined;
  }

  /**
   * Outlines one block card and says what is waiting to be published inside it.
   * @param {Element} entry The block card.
   * @param {Array<object>} blocks The unpublished elements of that block: its content, its settings, or both.
   */
  #markEntry(entry, blocks) {
    const signature = blocks.map((block) => `${block.key}|${block.status}|${block.changedAt}`).join("~");
    if (entry.getAttribute(BLOCK_MARKER_ATTRIBUTE) === signature && entry.__kobenMark?.isConnected) {
      return;
    }

    entry.setAttribute(BLOCK_MARKER_ATTRIBUTE, signature);
    this.#decoratedEntries.add(entry);

    // A card that is drawn without a shadow root — an unsupported block, a custom view that opted
    // out — is left alone rather than rewritten.
    const root = entry.shadowRoot;
    if (!root) {
      return;
    }

    let mark = entry.__kobenMark;
    if (!mark?.isConnected) {
      mark = document.createElement(BLOCK_MARK_ELEMENT);
      root.appendChild(mark);
    }

    const summary = describeBlocks(blocks);
    mark.label = summary.label;
    entry.__kobenMark = mark;

    // The tag steps aside for the card's own actions, so what it said has to be somewhere that
    // survives a hover. A card Umbraco has already given a tooltip keeps it.
    if (!entry.hasAttribute("title") || entry.__kobenTitle) {
      entry.setAttribute("title", summary.detail);
      entry.__kobenTitle = true;
    }
  }

  /**
   * Takes the outline back off a block card.
   * @param {Element} entry The block card.
   */
  #clearEntry(entry) {
    if (entry.hasAttribute(BLOCK_MARKER_ATTRIBUTE)) {
      entry.removeAttribute(BLOCK_MARKER_ATTRIBUTE);
    }

    if (entry.__kobenTitle) {
      entry.removeAttribute("title");
      entry.__kobenTitle = undefined;
    }

    entry.__kobenMark?.remove();
    entry.__kobenMark = undefined;
    this.#decoratedEntries.delete(entry);
  }

  /**
   * Puts a dot on every tab holding unpublished values, so an editor can see there is something to
   * publish on a tab they are not looking at.
   * @param {Array<Element>} tabs The content tabs currently rendered.
   */
  #markTabs(tabs) {
    const counts = new Map();
    for (const change of this.#tabbedChanges()) {
      if (!change.tab) {
        continue;
      }

      const slug = encodeFolderName(change.tab);
      counts.set(slug, (counts.get(slug) ?? 0) + 1);
    }

    for (const tab of tabs) {
      for (const badge of tab.children) {
        if (badge.localName === TAB_BADGE_ELEMENT && badge !== tab.__kobenDot) {
          this.#styleBadgeRoot(badge.shadowRoot, BADGE_STYLES);
        }
      }

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
   * The changes whose tabs this workspace's own tabs are counting: the document's properties, or —
   * in a block workspace — the properties of the block being edited.
   * @returns {Array<object>} The changes to count.
   */
  #tabbedChanges() {
    if (!this.#isBlockWorkspace) {
      return this.#changes;
    }

    return [this.#workspace?.content, this.#workspace?.settings]
      .map((element) => element?.getUnique?.())
      .flatMap((key) => (key && this.#blocksByKey.get(key)?.properties) || []);
  }

  /**
   * Adds the dot to one tab, reusing Umbraco's own badge so it sits where a tab's badges belong.
   * @param {Element} tab The tab element.
   * @param {number} count How many of the tab's values are unpublished.
   */
  #markTab(tab, count) {
    const label = plural(count, "unpublished change");

    // Umbraco puts its own badge — the "!" of a tab with invalid values — in the same corner, and
    // only while the tab is not the one open. The dot steps aside rather than cover it.
    const crowded = [...tab.children].some(
      (child) => child.localName === TAB_BADGE_ELEMENT && child !== tab.__kobenDot
    );
    const signature = `${label}|${crowded}`;
    if (tab.getAttribute(TAB_MARKER_ATTRIBUTE) === signature && tab.__kobenDot?.isConnected) {
      return;
    }

    tab.setAttribute(TAB_MARKER_ATTRIBUTE, signature);

    let dot = tab.__kobenDot;
    if (!dot?.isConnected) {
      dot = document.createElement(TAB_BADGE_ELEMENT);
      dot.setAttribute("slot", "extra");
      dot.setAttribute("color", "warning");
      tab.appendChild(dot);
    }

    // The badge renders its shadow root on connect, so it can be sized straight away.
    adoptStyles(dot.shadowRoot, DOT_STYLES);

    dot.setAttribute("title", label);
    if (crowded) {
      dot.style.setProperty("--uui-badge-inset", CROWDED_TAB_INSET);
    } else {
      dot.style.removeProperty("--uui-badge-inset");
    }

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
    const detail = summariseBlockCounts(change.blocks ?? []);
    const signature = `${change.culture ?? ""}|${change.segment ?? ""}|${change.changedBy}|${change.changedAt}|${detail}`;
    const isIntact =
      property.__kobenFlag?.isConnected &&
      property.__kobenLayout?.isConnected &&
      property.__kobenLayout.hasAttribute(HIGHLIGHT_ATTRIBUTE);
    if (property.getAttribute(MARKER_ATTRIBUTE) === signature && isIntact) {
      return;
    }

    property.setAttribute(MARKER_ATTRIBUTE, signature);
    this.#decorated.add(property);

    const layout = await this.#layoutOf(property);
    if (!layout || this.#isDestroyed || !property.isConnected || !property.hasAttribute(MARKER_ATTRIBUTE)) {
      return;
    }

    // The property element itself is laid out inline, so the highlight goes on the layout inside it,
    // which is the grid holding both the label and the editor. The stylesheet draws it; the attribute
    // switches it on.
    adoptStyles(layout.shadowRoot, LAYOUT_STYLES);
    this.#styledLayouts.add(layout);
    layout.setAttribute(HIGHLIGHT_ATTRIBUTE, "");

    let flag = layout.querySelector(FLAG_ELEMENT);
    if (!flag) {
      flag = document.createElement(FLAG_ELEMENT);
      flag.setAttribute("slot", "description");
      layout.appendChild(flag);
    }

    flag.changedBy = change.changedBy;
    flag.changedAt = change.changedAt;
    flag.detail = detail;
    property.__kobenFlag = flag;
    property.__kobenLayout = layout;
  }

  /**
   * Waits for a property to finish rendering and returns the layout its label lives in. A block's
   * property and the document's own are drawn by the same elements once past their host, so both
   * are found the same way.
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
    property.__kobenLayout?.removeAttribute(HIGHLIGHT_ATTRIBUTE);
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

    for (const entry of [...this.#decoratedEntries]) {
      this.#clearEntry(entry);
    }

    for (const tab of [...this.#decoratedTabs]) {
      this.#clearTab(tab);
    }

    for (const layout of this.#styledLayouts) {
      layout.removeAttribute(HIGHLIGHT_ATTRIBUTE);
      dropStyles(layout.shadowRoot, LAYOUT_STYLES);
    }
    this.#styledLayouts.clear();

    for (const entry of this.#styledEntries) {
      dropStyles(entry.shadowRoot, ENTRY_STYLES);
    }
    this.#styledEntries.clear();

    for (const root of this.#styledBadgeRoots) {
      dropStyles(root, BADGE_STYLES);
    }
    this.#styledBadgeRoots.clear();

    super.destroy();
  }
}

/**
 * Says what is waiting to be published inside one block, for the tag on its card.
 * @param {Array<object>} blocks The block's unpublished elements: its content, its settings, or both.
 * @returns {{label: string, detail: string}} The tag's text and its tooltip.
 */
function describeBlocks(blocks) {
  const latest = blocks.reduce((newest, block) => (new Date(block.changedAt) > new Date(newest.changedAt) ? block : newest));
  const credit = `${latest.changedBy} · ${formatElapsed(latest.changedAt)}`;

  if (blocks.some((block) => block.status === BLOCK_ADDED)) {
    return { label: "New", detail: `Added since this page was published · ${credit}` };
  }

  const changed = blocks.reduce((total, block) => total + (block.properties?.length ?? 0), 0);
  const settingsOnly = blocks.every((block) => block.scope === SETTINGS_SCOPE);
  const what = settingsOnly ? plural(changed, "unpublished settings change") : plural(changed, "unpublished change");

  return { label: "Edited", detail: `${what} · ${credit}` };
}

/**
 * Says how many of a property's blocks changed, for the tag under its label. The property is
 * already flagged; this is what publishing it would do that the value alone does not show.
 * @param {Array<object>} blocks The property's unpublished blocks.
 * @returns {string} The summary, or an empty string for a property that holds no blocks.
 */
function summariseBlockCounts(blocks) {
  if (blocks.length === 0) {
    return "";
  }

  const removed = blocks.filter((block) => block.status === BLOCK_REMOVED).length;
  const present = new Set(blocks.filter((block) => block.status !== BLOCK_REMOVED).map((block) => block.ownerKey)).size;

  const parts = [];
  if (present > 0) {
    parts.push(`${plural(present, "block")} changed`);
  }
  if (removed > 0) {
    parts.push(`${removed} removed`);
  }

  return parts.join(", ");
}

export { KobenPendingChangesWorkspaceContext as api, KobenPendingTreeSignElement as element };
export default KobenPendingChangesWorkspaceContext;
