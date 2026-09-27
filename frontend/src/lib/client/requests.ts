export class ApiError extends Error {
  constructor(message: string, public status = 0) { super(message); }
}

export async function requestJson<T>(url: string, init: RequestInit = {}, fetcher: typeof fetch = fetch): Promise<T> {
  let response: Response;
  try {
    response = await fetcher(url, {...init, signal: init.signal || AbortSignal.timeout(20000)});
  } catch {
    throw new ApiError("Koneksi terputus atau terlalu lama. Periksa koneksi lalu coba kembali.");
  }
  let payload: T & {error?: string};
  try { payload = await response.json(); }
  catch { throw new ApiError("Respons layanan belum dapat dibaca. Coba kembali.", response.status); }
  if (!response.ok) throw new ApiError(payload.error || "Permintaan belum berhasil. Coba kembali.", response.status);
  return payload;
}

/** Share an in-flight request; a failed attempt never locks subsequent retries. */
export function singleFlight<T>() {
  let pending: Promise<T> | undefined;
  return (operation: () => Promise<T>) => {
    if (!pending) pending = Promise.resolve().then(operation).finally(() => { pending = undefined; });
    return pending;
  };
}

export async function logoutSession(fetcher: typeof fetch = fetch) {
  try { await requestJson<{ok: boolean}>("/api/auth/logout", {method: "POST"}, fetcher); }
  catch {
    throw new ApiError("Belum berhasil keluar. Sesi Anda mungkin masih aktif. Periksa koneksi lalu coba keluar kembali.");
  }
}
