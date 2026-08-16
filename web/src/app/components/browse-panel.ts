import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BrowsedNodeDto } from '../core/api.types';
import { elide } from '../core/format';
import { HubClient } from '../core/hub-client';
import { MonitorApi } from '../core/monitor-api';
import { MonitorStore } from '../core/monitor-store';

interface TreeNode {
  data: BrowsedNodeDto;
  expanded: boolean;
  loading: boolean;
  error: string | null;
  children: TreeNode[] | null;
}

interface Row {
  node: TreeNode;
  depth: number;
}

/**
 * Address-space browser with subscribe and unsubscribe.
 *
 * Lazy, one level per expansion, because that is what the API offers and because
 * a real server's address space is far too large to fetch up front. Each level
 * arrives with the current value of its variables already read, so the operator
 * can tell which of six similarly-named tags is the live one before subscribing
 * to any of them.
 */
@Component({
  selector: 'app-browse-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
  template: `
    <section class="panel">
      <header>
        <span class="heading">ADDRESS SPACE</span>
        <button class="ghost" type="button" (click)="reload()" [disabled]="rootLoading()">
          {{ rootLoading() ? '…' : 'refresh' }}
        </button>
      </header>

      @if (rootError(); as error) {
        <p class="error">{{ error }}</p>
      }

      <div class="tree" role="tree">
        @for (row of rows(); track row.node.data.nodeId) {
          <div
            class="row"
            role="treeitem"
            [attr.aria-expanded]="row.node.data.hasChildren ? row.node.expanded : null"
            [style.padding-left.px]="8 + row.depth * 12"
            [class.monitored]="isMonitored(row.node.data)"
          >
            <button
              class="twisty"
              type="button"
              [disabled]="!row.node.data.hasChildren"
              [attr.aria-label]="row.node.expanded ? 'Collapse' : 'Expand'"
              (click)="toggle(row.node)"
            >
              @if (row.node.loading) {
                <span class="spin">·</span>
              } @else if (row.node.data.hasChildren) {
                {{ row.node.expanded ? '▾' : '▸' }}
              } @else {
                <span class="leaf">·</span>
              }
            </button>

            <span class="kind" [class.variable]="row.node.data.isVariable">{{
              row.node.data.isVariable ? 'V' : 'O'
            }}</span>

            <span class="label" [title]="row.node.data.nodeId">{{ row.node.data.displayName }}</span>

            @if (row.node.data.displayValue !== undefined) {
              <span class="peek" [class.bad]="row.node.data.quality !== 'Good'">{{
                short(row.node.data.displayValue)
              }}</span>
            }

            @if (row.node.data.isVariable) {
              @if (isMonitored(row.node.data)) {
                <button
                  class="act drop"
                  type="button"
                  title="Stop monitoring"
                  (click)="unsubscribe(row.node.data)"
                >
                  −
                </button>
              } @else {
                <button
                  class="act add"
                  type="button"
                  title="Monitor this node"
                  (click)="subscribe(row.node.data)"
                >
                  +
                </button>
              }
            }
          </div>

          @if (row.node.error; as error) {
            <p class="error nested" [style.padding-left.px]="20 + row.depth * 12">{{ error }}</p>
          }
        }
      </div>

      <form class="manual" (ngSubmit)="subscribeManual()">
        <label for="address">ADD BY ADDRESS</label>
        <div class="entry">
          <input
            id="address"
            name="address"
            type="text"
            autocomplete="off"
            spellcheck="false"
            placeholder="ns=1;i=1756 or Tank/TankLevel"
            [(ngModel)]="manualAddress"
          />
          <button type="submit" [disabled]="busy()">add</button>
        </div>
        @if (manualError(); as error) {
          <p class="error">{{ error }}</p>
        }
      </form>
    </section>
  `,
  styleUrl: './browse-panel.scss',
})
export class BrowsePanel {
  private readonly api = inject(MonitorApi);
  private readonly hub = inject(HubClient);
  private readonly store = inject(MonitorStore);

  private readonly roots = signal<TreeNode[]>([]);

  readonly rootLoading = signal(false);
  readonly rootError = signal<string | null>(null);
  readonly manualError = signal<string | null>(null);
  readonly busy = signal(false);

  manualAddress = '';

  /**
   * The tree flattened for rendering.
   *
   * A flat list keeps the template a single `@for` with no recursive component,
   * which matters because a recursive component would create one change-detection
   * boundary per level of an address space that can be a dozen deep.
   */
  readonly rows = computed<Row[]>(() => flatten(this.roots(), 0));

  private readonly monitoredIds = computed(
    () => new Set(this.store.nodes().map((view) => view.node.id)),
  );

  constructor() {
    void this.reload();
  }

  isMonitored(data: BrowsedNodeDto): boolean {
    // The server's own flag covers nodes configured by node id. The client-side
    // check additionally catches a node the operator has just added, before the
    // browse level is re-fetched.
    return data.isMonitored || this.monitoredIds().has(data.nodeId);
  }

  short(value: string | undefined): string {
    return value === undefined ? '' : elide(value, 18);
  }

  async reload(): Promise<void> {
    this.rootLoading.set(true);
    this.rootError.set(null);

    try {
      const result = await this.api.browse();
      this.roots.set(result.children.map(toTreeNode));
    } catch (error) {
      this.rootError.set(describe(error));
      this.roots.set([]);
    } finally {
      this.rootLoading.set(false);
    }
  }

  async toggle(node: TreeNode): Promise<void> {
    if (!node.data.hasChildren) return;

    if (node.expanded) {
      this.patch(node.data.nodeId, (n) => ({ ...n, expanded: false }));
      return;
    }

    if (node.children) {
      this.patch(node.data.nodeId, (n) => ({ ...n, expanded: true }));
      return;
    }

    this.patch(node.data.nodeId, (n) => ({ ...n, loading: true, error: null }));

    try {
      const result = await this.api.browse(node.data.nodeId);
      this.patch(node.data.nodeId, (n) => ({
        ...n,
        expanded: true,
        loading: false,
        children: result.children.map(toTreeNode),
      }));
    } catch (error) {
      this.patch(node.data.nodeId, (n) => ({ ...n, loading: false, error: describe(error) }));
    }
  }

  async subscribe(data: BrowsedNodeDto): Promise<void> {
    this.busy.set(true);
    try {
      await this.api.subscribe({
        address: data.nodeId,
        displayName: data.displayName || data.browseName,
      });
      // The hub broadcasts NodesChanged, but that message carries node metadata
      // only. A snapshot also brings the new node's current value and window, so
      // its card is populated rather than blank until the next data change.
      await this.hub.resync();
      this.patch(data.nodeId, (n) => ({ ...n, data: { ...n.data, isMonitored: true } }));
    } catch (error) {
      this.patch(data.nodeId, (n) => ({ ...n, error: describe(error) }));
    } finally {
      this.busy.set(false);
    }
  }

  async unsubscribe(data: BrowsedNodeDto): Promise<void> {
    this.busy.set(true);
    try {
      await this.api.unsubscribe(data.nodeId);
      await this.hub.resync();
      this.patch(data.nodeId, (n) => ({ ...n, data: { ...n.data, isMonitored: false } }));
    } catch (error) {
      this.patch(data.nodeId, (n) => ({ ...n, error: describe(error) }));
    } finally {
      this.busy.set(false);
    }
  }

  async subscribeManual(): Promise<void> {
    const address = this.manualAddress.trim();
    if (!address) return;

    this.busy.set(true);
    this.manualError.set(null);

    try {
      await this.api.subscribe({ address });
      await this.hub.resync();
      this.manualAddress = '';
    } catch (error) {
      this.manualError.set(describe(error));
    } finally {
      this.busy.set(false);
    }
  }

  /** Replaces one node in the tree, rebuilding only the branch that contains it. */
  private patch(nodeId: string, update: (node: TreeNode) => TreeNode): void {
    this.roots.update((roots) => patchIn(roots, nodeId, update));
  }
}

function toTreeNode(data: BrowsedNodeDto): TreeNode {
  return { data, expanded: false, loading: false, error: null, children: null };
}

function flatten(nodes: TreeNode[], depth: number): Row[] {
  const rows: Row[] = [];

  for (const node of nodes) {
    rows.push({ node, depth });
    if (node.expanded && node.children) rows.push(...flatten(node.children, depth + 1));
  }

  return rows;
}

function patchIn(
  nodes: TreeNode[],
  nodeId: string,
  update: (node: TreeNode) => TreeNode,
): TreeNode[] {
  return nodes.map((node) => {
    if (node.data.nodeId === nodeId) return update(node);
    if (!node.children) return node;

    const children = patchIn(node.children, nodeId, update);
    return children === node.children ? node : { ...node, children };
  });
}

/**
 * Turns a failed request into something worth reading.
 *
 * The API answers with RFC 7807 problem details, and its `detail` is the useful
 * part — "does not resolve to a node on this server" rather than "Http failure
 * response for http://localhost:8080/api/nodes: 404 Not Found".
 */
function describe(error: unknown): string {
  if (typeof error === 'object' && error !== null) {
    const body = (error as { error?: { detail?: string; title?: string } }).error;
    if (body?.detail) return body.detail;
    if (body?.title) return body.title;

    const message = (error as { message?: string }).message;
    if (message) return message;
  }

  return 'Request failed.';
}
