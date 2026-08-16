/**
 * Mirrors of the wire contracts in `src/OpcMonitor.Api/Contracts.cs`.
 *
 * Hand-written rather than generated. The contract is small, stable and
 * deliberately flat, and a generator would add a build step and a schema
 * pipeline to save maintaining forty lines — while making it less obvious to a
 * reader what the browser actually receives.
 */

export type QualitySeverity = 'good' | 'uncertain' | 'bad';

export interface NodeReadingDto {
  nodeId: string;
  /** As delivered by the server: a JSON number, string or boolean. Absent when null. */
  value?: unknown;
  displayValue: string;
  quality: string;
  qualitySeverity: QualitySeverity;
  /** Device clock if the server supplied one, else the server clock, else receive time. */
  timestamp: string;
  receivedAt: string;
}

export interface MonitoredNodeDto {
  id: string;
  displayName: string;
  unit?: string;
  minimum?: number;
  maximum?: number;
}

export interface NodeStatusDto {
  node: MonitoredNodeDto;
  current?: NodeReadingDto;
  withinBand?: boolean;
  window: NodeReadingDto[];
}

export type OpcConnectionState =
  | 'disconnected'
  | 'connecting'
  | 'connected'
  | 'reconnecting'
  | 'faulted';

export interface ConnectionStatusDto {
  state: OpcConnectionState;
  endpointUrl: string;
  changedAt: string;
  attempt: number;
  detail?: string;
}

export interface SnapshotDto {
  connection: ConnectionStatusDto;
  nodes: NodeStatusDto[];
  serverTime: string;
}

export interface BrowsedNodeDto {
  nodeId: string;
  browseName: string;
  displayName: string;
  nodeClass: 'object' | 'variable';
  isVariable: boolean;
  hasChildren: boolean;
  isMonitored: boolean;
  dataType?: string;
  displayValue?: string;
  quality?: string;
}

export interface BrowseResultDto {
  nodeId: string;
  children: BrowsedNodeDto[];
}

export interface SubscribeRequest {
  address: string;
  displayName?: string;
  unit?: string;
  minimum?: number;
  maximum?: number;
}
