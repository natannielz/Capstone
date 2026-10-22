import { buyerTaskHrefs, isStaffActor } from '../../modules/identity/identity-workflow';

export type TaskFrameKind = 'store' | 'workspace' | 'pending';

/**
 * Which frame a page opens in (G-037, R2: one path per task). A buyer's own task page opens inside the storefront for someone
 * without back-office work; staff keep the workspace. Staff-only pages are always the workspace and never wait for the viewer.
 * `actions` is null while the viewer is still being read.
 */
export function taskFrame(pathname: string, actions: readonly string[] | null): TaskFrameKind {
  if (!buyerTaskHrefs().includes(pathname)) return 'workspace';
  if (actions === null) return 'pending';
  return isStaffActor(actions) ? 'workspace' : 'store';
}
