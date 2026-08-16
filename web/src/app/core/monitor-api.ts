import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { resolveApiBase } from './api-base';
import { BrowseResultDto, MonitoredNodeDto, NodeStatusDto, SubscribeRequest } from './api.types';

/**
 * The REST half of the contract: everything that is a request rather than a
 * stream.
 *
 * Browsing and subscribing are deliberately not hub methods. They are
 * request/response with a result the caller needs, they are initiated by one
 * operator rather than broadcast, and being plain HTTP means they can be
 * exercised with `curl` when the dashboard is the thing under suspicion.
 */
@Injectable({ providedIn: 'root' })
export class MonitorApi {
  private readonly http = inject(HttpClient);
  private readonly base = resolveApiBase();

  nodes(): Promise<NodeStatusDto[]> {
    return firstValueFrom(this.http.get<NodeStatusDto[]>(`${this.base}/api/nodes`));
  }

  /** Children of a node. Omit `nodeId` for the Objects folder. */
  browse(nodeId?: string): Promise<BrowseResultDto> {
    const url = nodeId
      ? `${this.base}/api/browse?nodeId=${encodeURIComponent(nodeId)}`
      : `${this.base}/api/browse`;

    return firstValueFrom(this.http.get<BrowseResultDto>(url));
  }

  subscribe(request: SubscribeRequest): Promise<MonitoredNodeDto> {
    return firstValueFrom(this.http.post<MonitoredNodeDto>(`${this.base}/api/nodes`, request));
  }

  unsubscribe(id: string): Promise<void> {
    // The route is a catch-all so ids containing slashes — every browse-path
    // node — survive the round trip. Encoding the whole id keeps a `#` or `?` in
    // a tag name from being read as URL syntax.
    return firstValueFrom(
      this.http.delete<void>(`${this.base}/api/nodes/${encodeURIComponent(id)}`),
    );
  }
}
