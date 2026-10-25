/**
 * Independent read-only snapshots of private Deribit REST resources.
 * Never render a rejected/malformed account response as an empty exchange state.
 * All acceptance is scoped to the active polling epoch, not to network completion order.
 */
export type ReadState = 'loading' | 'ready' | 'unavailable';
export type ReadSnapshot<T> = {
  state: ReadState;
  items: T[];
  sampledAtUnixMs: number | null;
};

export function emptyReadSnapshot<T>(): ReadSnapshot<T> {
  return { state: 'loading', items: [], sampledAtUnixMs: null };
}

export function unavailableReadSnapshot<T>(): ReadSnapshot<T> {
  return { state: 'unavailable', items: [], sampledAtUnixMs: null };
}

export function settleReadSnapshot<T>(
  response: PromiseSettledResult<unknown>,
  sampledAtUnixMs: number,
  isValid: (item: unknown) => item is T
): ReadSnapshot<T> {
  if (response.status !== 'fulfilled' ||
      !Array.isArray(response.value) ||
      !response.value.every(isValid) ||
      !Number.isFinite(sampledAtUnixMs) || sampledAtUnixMs <= 0)
    return unavailableReadSnapshot<T>();
  return { state: 'ready', items: response.value as T[], sampledAtUnixMs };
}

export function settleAccountPair<T>(
  btc: PromiseSettledResult<unknown>,
  eth: PromiseSettledResult<unknown>,
  sampledAtUnixMs: number,
  isValid: (item: unknown) => item is T
): ReadSnapshot<T> {
  if (btc.status !== 'fulfilled' || eth.status !== 'fulfilled')
    return unavailableReadSnapshot<T>();
  return settleReadSnapshot<T>(
    { status: 'fulfilled', value: [btc.value, eth.value] },
    sampledAtUnixMs, isValid
  );
}

export function snapshotCount<T>(snapshot: ReadSnapshot<T>): number | null {
  return snapshot.state === 'ready' ? snapshot.items.length : null;
}

/**
 * A new epoch invalidates all older responses. Concurrent polls cannot start
 * inside one epoch; cleanup discards both the old response and its ownership.
 */
export class PrivateReadGate {
  private epoch = 0;
  private busy = false;

  begin(): number | null {
    if (this.busy) return null;
    this.busy = true;
    return this.epoch;
  }

  accepts(epoch: number): boolean {
    return this.busy && epoch === this.epoch;
  }

  finish(epoch: number): void {
    if (epoch === this.epoch) this.busy = false;
  }

  invalidate(): void {
    this.epoch += 1;
    this.busy = false;
  }
}
