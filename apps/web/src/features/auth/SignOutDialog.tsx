import { useEffect, useRef, useState } from 'react';

interface Props {
  /** Whether this browser is paired with a PC. Without a pairing there is nothing to keep, so no question is asked. */
  paired: boolean;
  onConfirm: (removePairing: boolean) => void;
  onCancel: () => void;
}

/** Sign-out asks one question: also remove the pairing from this browser? The default keeps it, so signing in again needs no new pairing code. */
export function SignOutDialog({ paired, onConfirm, onCancel }: Props) {
  const dialog = useRef<HTMLDialogElement>(null);
  const [remove, setRemove] = useState(false);
  useEffect(() => { dialog.current?.showModal(); }, []);
  return (
    <dialog ref={dialog} className="properties" aria-labelledby="signout-title" onClose={onCancel} onCancel={onCancel} data-testid="signout-dialog">
      <h2 id="signout-title">Sign out</h2>
      {paired && (
        <>
          <label className="check"><input type="checkbox" checked={remove} onChange={(e) => setRemove(e.target.checked)} />Also remove the pairing from this browser?</label>
          <p className="hint">{remove
            ? 'This browser will have to pair with your PC again with a new code. Use this on a shared or public computer.'
            : 'The pairing stays, so signing in again on this browser needs no new code. Do not leave it on a shared or public computer.'}</p>
        </>
      )}
      <div className="actions">
        <button type="button" className="ghost" onClick={onCancel}>Cancel</button>
        <button type="button" onClick={() => onConfirm(remove)} autoFocus>Sign out</button>
      </div>
    </dialog>
  );
}
