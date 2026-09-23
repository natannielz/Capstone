"use client";

import { useEffect, useRef, useState, useSyncExternalStore, type FormEvent } from "react";
import Link from "next/link";
import { Camera, Eye, EyeOff, Save } from "lucide-react";
import { toast } from "sonner";
import { Art } from "./art";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { clearCustomerCart } from "@/lib/client/customer-cart";
import { clearCartForAccount } from "@/lib/client/cart-storage";
import { ApiError, requestJson as request } from "@/lib/client/requests";
import {captureCustomerProfileDraft, clearCustomerProfileDraft, clearCustomerProfileDraftSnapshot, loadCustomerProfileDraft, profileValues, reconcileProfileValues, sameProfileValues, saveCustomerProfileDraft, type ProfileValues} from "@/lib/client/customer-profile-draft";
import { Field, FieldDescription, FieldError, FieldGroup, FieldLabel } from "@/components/ui/field";
import { ROLE_LABELS } from "@/lib/domain/accounts";
import type { WorkspaceContext } from "./workspace";

type PasswordValues = { current: string; password: string; confirm: string };
type Errors<T> = Partial<Record<keyof T, string>>;
type ProfileProps = WorkspaceContext & { onDirtyChange?: (dirty: boolean) => void };

const EMPTY_PASSWORDS: PasswordValues = { current: "", password: "", confirm: "" };
const PASSWORD_FIELDS = { current: "old-password", password: "new-password", confirm: "confirm-password" };
const subscribeToHydration = () => () => {};
const clientReady = () => true;
const serverReady = () => false;

function focusField(id: string) {
  requestAnimationFrame(() => document.getElementById(id)?.focus());
}

function errorMessage(error: unknown) {
  return error instanceof Error ? error.message : "Permintaan belum berhasil. Coba kembali.";
}

export function Profile(props: ProfileProps) {
  const ready = useSyncExternalStore(subscribeToHydration, clientReady, serverReady);
  // Never hydrate a server-rendered form with another tab/actor's browser draft.
  // Keying the entire editor also resets passwords immediately on actor changes.
  return ready ? <ProfileEditor key={`${props.actor.role}:${props.actor.id}`} {...props}/> : <p role="status">Menyiapkan profil…</p>;
}

function ProfileEditor({ actor, s, refresh, onDirtyChange }: ProfileProps) {
  const [initial] = useState(() => {
    const baseline = profileValues(actor);
    const {draft, available} = actor.role === "customer" ? loadCustomerProfileDraft(actor.id) : {draft: undefined, available: true};
    const merged = draft ? reconcileProfileValues(draft.baseline, draft.values, baseline, true) : {values: baseline, changedOnServer: []};
    const restored = Boolean(draft && !sameProfileValues(merged.values, baseline));
    return {baseline, values: merged.values, restored, available, serverChanged: restored && merged.changedOnServer.length > 0, snapshot: actor.role === "customer" ? captureCustomerProfileDraft(actor.id) : undefined};
  });
  const [values, setValues] = useState<ProfileValues>(initial.values);
  const [savedValues, setSavedValues] = useState(initial.baseline);
  const [observedServer, setObservedServer] = useState(initial.baseline);
  const [restoredDraft, setRestoredDraft] = useState(initial.restored);
  const [draftAvailable, setDraftAvailable] = useState(initial.available);
  const [serverChanged, setServerChanged] = useState(initial.serverChanged);
  const [cleanupWarning, setCleanupWarning] = useState("");
  const [draftSnapshot, setDraftSnapshot] = useState(initial.snapshot);
  const mounted = useRef(true);
  const [passwords, setPasswords] = useState<PasswordValues>(EMPTY_PASSWORDS);
  const [visiblePasswords, setVisiblePasswords] = useState({current: false, password: false, confirm: false});
  const [sessionExpired, setSessionExpired] = useState(false);
  const [profileErrors, setProfileErrors] = useState<Errors<ProfileValues>>({});
  const [passwordErrors, setPasswordErrors] = useState<Errors<PasswordValues>>({});
  const [saveError, setSaveError] = useState("");
  const [passwordError, setPasswordError] = useState("");
  const [avatarError, setAvatarError] = useState("");
  const [avatarInvalid, setAvatarInvalid] = useState(false);
  const [savedMessage, setSavedMessage] = useState("");
  const [pending, setPending] = useState<"profile" | "password" | "avatar" | null>(null);
  const busy = pending !== null;
  const profileDirty = (Object.keys(values) as (keyof ProfileValues)[])
    .some((key) => values[key] !== savedValues[key]);
  const passwordDirty = Object.values(passwords).some(Boolean);
  const dirty = profileDirty || passwordDirty;
  const loginHref = `${actor.role === "customer" ? "/customer/login" : "/staff/login"}?notice=session-expired&next=${encodeURIComponent(actor.role === "customer" ? "/account" : "/workspace?view=profile")}`;
  const incoming = profileValues(actor);
  if (!sameProfileValues(incoming, observedServer)) {
    const merged = reconcileProfileValues(savedValues, values, incoming);
    setObservedServer(incoming);
    setSavedValues(incoming);
    setValues(merged.values);
    setServerChanged(!sameProfileValues(merged.values, incoming) && merged.changedOnServer.length > 0);
  }

  function checkSession(error: unknown) {
    if (error instanceof ApiError && error.status === 401) setSessionExpired(true);
  }

  function passwordToggle(key: keyof PasswordValues, label: string) {
    return <Button className="profile-password-toggle" type="button" variant="ghost" size="icon" disabled={busy} aria-label={`${visiblePasswords[key] ? "Sembunyikan" : "Tampilkan"} ${label}`} aria-pressed={visiblePasswords[key]} aria-controls={PASSWORD_FIELDS[key]} onClick={() => setVisiblePasswords(previous => ({...previous, [key]: !previous[key]}))}>{visiblePasswords[key] ? <EyeOff aria-hidden="true"/> : <Eye aria-hidden="true"/>}</Button>;
  }

  useEffect(() => {
    onDirtyChange?.(dirty);
  }, [dirty, onDirtyChange]);

  useEffect(() => () => onDirtyChange?.(false), [onDirtyChange]);
  useEffect(() => {mounted.current = true; return () => {mounted.current = false;};}, []);
  useEffect(() => {
    if (actor.role === "customer" && !profileDirty) clearCustomerProfileDraftSnapshot(actor.id, draftSnapshot);
  }, [actor.id, actor.role, profileDirty, draftSnapshot]);

  function discardProfile(useServer = false) {
    const cleared = actor.role !== "customer" || clearCustomerProfileDraft(actor.id);
    setCleanupWarning(cleared ? "" : "Isian di halaman ini telah dibatalkan. Pembersihan draf di penyimpanan browser belum dapat dipastikan; draf lama mungkin muncul lagi setelah halaman dimuat ulang.");
    setValues(savedValues); setProfileErrors({}); setSaveError(""); setRestoredDraft(false); setServerChanged(false);
    setSavedMessage(useServer ? "Data terbaru dari server digunakan." : "Perubahan data pribadi dibatalkan.");
  }

  function editProfile(key: keyof ProfileValues, value: string) {
    const next = {...values, [key]: value};
    // A storage write happens inside the input event, not in a delayed effect.
    if (actor.role === "customer") {
      const saved = saveCustomerProfileDraft(actor.id, savedValues, next);
      setDraftAvailable(saved);
      setDraftSnapshot(saved ? captureCustomerProfileDraft(actor.id) : undefined);
    }
    setValues(next);
    setProfileErrors((previous) => ({ ...previous, [key]: undefined }));
    setSaveError("");
    setSavedMessage("");
  }

  function editPassword(key: keyof PasswordValues, value: string) {
    setPasswords((previous) => ({ ...previous, [key]: value }));
    setPasswordErrors((previous) => ({
      ...previous,
      [key]: undefined,
      ...(key === "password" ? { confirm: undefined } : {}),
    }));
    setPasswordError("");
  }

  async function save(event: FormEvent) {
    event.preventDefault();
    if (busy) return;
    const next = {
      name: values.name.trim(),
      phone: values.phone.trim(),
      position: values.position.trim(),
      address: values.address.trim(),
    };
    const errors: Errors<ProfileValues> = {};
    if (!next.name) errors.name = "Masukkan nama lengkap.";
    else if (next.name.length > 100) errors.name = "Nama lengkap maksimal 100 karakter.";
    if (next.phone.length > 30) errors.phone = "Nomor kontak maksimal 30 karakter.";
    if (next.position.length > 100) errors.position = "Jabatan maksimal 100 karakter.";
    if (next.address.length > 500) errors.address = "Alamat maksimal 500 karakter.";
    setProfileErrors(errors);
    setSaveError("");
    setSavedMessage("");
    const invalid = Object.keys(errors)[0];
    if (invalid) {
      focusField("profile-" + invalid);
      return;
    }
    const submittedValues = {...values};
    const submittedDraft = draftSnapshot;
    setPending("profile");
    try {
      await request("/api/profile", {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(actor.role === "customer" ? next : {name: next.name, phone: next.phone, position: next.position}),
      });
      if (!mounted.current) return;
      setValues(current => reconcileProfileValues(submittedValues, current, next).values);
      setSavedValues(next);
      const cleanup = actor.role === "customer" ? clearCustomerProfileDraftSnapshot(actor.id, submittedDraft) : "cleared";
      setCleanupWarning(cleanup === "unavailable" ? "Profil berhasil disimpan di server. Pembersihan draf lokal belum dapat dipastikan karena penyimpanan browser tidak tersedia." : "");
      setRestoredDraft(false); setServerChanged(false);
      setSavedMessage("Perubahan profil telah disimpan.");
      await refresh();
    } catch (error) {
      if (!mounted.current) return;
      checkSession(error);
      setSaveError(errorMessage(error));
      focusField("profile-save-error");
    } finally {
      if (mounted.current) setPending(null);
    }
  }

  async function upload(file: File) {
    if (busy) return;
    setAvatarError("");
    setAvatarInvalid(false);
    if (file.size > 2 * 1024 * 1024 || (file.type && !["image/png", "image/jpeg"].includes(file.type))) {
      setAvatarInvalid(true);
      setAvatarError(file.size > 2 * 1024 * 1024 ? "Pilih foto maksimal 2 MB." : "Pilih foto dengan format PNG atau JPG.");
      focusField("profile-avatar");
      return;
    }
    setPending("avatar");
    try {
      const form = new FormData();
      form.set("file", file);
      await request("/api/profile/avatar", { method: "POST", body: form });
      await refresh();
      toast.success("Foto profil diperbarui.");
    } catch (error) {
      checkSession(error);
      setAvatarError(errorMessage(error));
      focusField("profile-photo-error");
    } finally {
      setPending(null);
    }
  }

  async function changePassword(event: FormEvent) {
    event.preventDefault();
    if (busy) return;
    if (profileDirty) {
      setPasswordError("Simpan atau batalkan perubahan data pribadi sebelum mengubah kata sandi.");
      focusField("profile-password-error");
      return;
    }
    const errors: Errors<PasswordValues> = {};
    if (!passwords.current) errors.current = "Masukkan kata sandi saat ini.";
    if (passwords.password.length < 12 || passwords.password.length > 128) {
      errors.password = "Gunakan kata sandi baru 12–128 karakter.";
    }
    if (!passwords.confirm) errors.confirm = "Ulangi kata sandi baru.";
    else if (passwords.password !== passwords.confirm) errors.confirm = "Kata sandi yang diulang belum sama. Periksa kembali.";
    setPasswordErrors(errors);
    setPasswordError("");
    const invalid = (Object.keys(errors) as (keyof PasswordValues)[])[0];
    if (invalid) {
      focusField(PASSWORD_FIELDS[invalid]);
      return;
    }
    setPending("password");
    try {
      await request("/api/profile", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ currentPassword: passwords.current, newPassword: passwords.password }),
      });
      setPasswords(EMPTY_PASSWORDS);
      setVisiblePasswords({current: false, password: false, confirm: false});
      if (actor.role === "customer") clearCustomerProfileDraft(actor.id);
      onDirtyChange?.(false);
      clearCartForAccount(actor.id);
      await clearCustomerCart(actor.id);
      window.location.assign(actor.role === "customer" ? "/customer/login?notice=password-changed&next=%2Faccount" : "/staff/login?notice=password-changed");
    } catch (error) {
      checkSession(error);
      const message = errorMessage(error);
      if (message === "Kata sandi saat ini tidak sesuai.") {
        setPasswordErrors({ current: message });
        focusField("old-password");
      } else {
        setPasswordError(message);
        focusField("profile-password-error");
      }
      setPending(null);
    }
  }

  return (
    <div className="profile-layout profile-v14">
      <aside className="panel profile-card">
        <Art className="profile-photo" src={actor.avatar || "/images/avatars/" + (actor.role === "pic" ? "pic-a" : actor.role === "customer" ? "pic-b" : actor.role) + ".png"} alt={"Foto profil " + actor.name} />
        <h2>{actor.name}</h2>
        <p>{ROLE_LABELS[actor.role]}</p>
        <label className="upload-label" htmlFor="profile-avatar">
          <Camera size={15} aria-hidden="true" />{pending === "avatar" ? "Mengunggah…" : "Ubah foto"}
          <input
            id="profile-avatar"
            aria-label="Ubah foto profil"
            aria-describedby={"profile-photo-help" + (avatarError ? " profile-photo-error" : "")}
            aria-invalid={avatarInvalid || undefined}
            type="file"
            accept="image/png,image/jpeg"
            disabled={busy}
            onChange={(event) => {
              const file = event.target.files?.[0];
              if (file) void upload(file);
              event.target.value = "";
            }}
          />
        </label>
        <p id="profile-photo-help" className="profile-photo-help">PNG atau JPG, maksimal 2 MB.<br />Foto bawaan adalah ilustrasi akun demo.</p>
        {avatarError && <FieldError id="profile-photo-error" tabIndex={-1}>{avatarError}</FieldError>}
      </aside>
      <div className="profile-details">
        {sessionExpired && <Alert className="profile-session-notice"><AlertDescription><strong>Sesi Anda telah berakhir.</strong><p>Masuk kembali sebelum menyimpan perubahan. Kata sandi yang Anda ketik tidak disimpan sebagai draf.</p><Button asChild variant="outline"><Link href={loginHref}>Masuk kembali</Link></Button></AlertDescription></Alert>}
        <section className="panel profile-section" aria-labelledby="profile-personal-title">
          <header className="panel-title profile-section-header"><div><h2 id="profile-personal-title">Data pribadi</h2><p>Perbarui nama dan informasi kontak.</p></div></header>
          <form className="profile-form" onSubmit={save} aria-busy={pending === "profile"} noValidate>
            {saveError && <Alert id="profile-save-error" variant="destructive" tabIndex={-1}><AlertDescription>{saveError}</AlertDescription></Alert>}
            {cleanupWarning && <Alert className="profile-draft-notice"><AlertDescription>{cleanupWarning}</AlertDescription></Alert>}
            {profileDirty && serverChanged && <Alert className="profile-draft-notice"><AlertDescription><strong>Data profil di server telah berubah.</strong><p>Isian yang Anda ubah tetap dipertahankan. Kolom lainnya sudah memakai data terbaru. Periksa kembali sebelum menyimpan.</p><Button variant="outline" type="button" disabled={busy} onClick={() => discardProfile(true)}>Buang perubahan & gunakan data terbaru</Button></AlertDescription></Alert>}
            {actor.role === "customer" && profileDirty && <div className={`profile-draft-status${draftAvailable ? "" : " profile-draft-unavailable"}`} role="status"><strong>{!draftAvailable ? "Draf tidak dapat disimpan di tab ini." : restoredDraft ? "Draf data pribadi dipulihkan." : "Draf data pribadi tersimpan di tab ini."}</strong><p>{draftAvailable ? "Anda dapat kembali ke profil melalui Back/Forward tanpa kehilangan isian ini. Simpan perubahan untuk memperbarui akun." : "Simpan perubahan sebelum memakai Back/Forward atau meninggalkan halaman. Periksa izin dan ruang penyimpanan browser."} Kata sandi tidak disimpan dalam draf.</p></div>}
            <FieldGroup className="profile-fields profile-personal-fields">
              <Field data-disabled={busy} data-invalid={Boolean(profileErrors.name)}>
                <FieldLabel htmlFor="profile-name">Nama lengkap</FieldLabel>
                <Input id="profile-name" autoComplete="name" value={values.name} onChange={(event) => editProfile("name", event.target.value)} maxLength={100} required disabled={busy} aria-invalid={Boolean(profileErrors.name)} aria-describedby={profileErrors.name ? "profile-name-error" : undefined} />
                {profileErrors.name && <FieldError id="profile-name-error">{profileErrors.name}</FieldError>}
              </Field>
              <Field data-disabled={busy} data-invalid={Boolean(profileErrors.phone)}>
                <FieldLabel htmlFor="profile-phone">Nomor kontak (opsional)</FieldLabel>
                <Input id="profile-phone" type="tel" autoComplete="tel" placeholder="Contoh: 081234567890" value={values.phone} onChange={(event) => editProfile("phone", event.target.value)} maxLength={30} disabled={busy} aria-invalid={Boolean(profileErrors.phone)} aria-describedby={profileErrors.phone ? "profile-phone-error" : undefined} />
                {profileErrors.phone && <FieldError id="profile-phone-error">{profileErrors.phone}</FieldError>}
              </Field>
              {actor.role !== "customer" && <Field data-disabled={busy} data-invalid={Boolean(profileErrors.position)}>
                <FieldLabel htmlFor="profile-position">Jabatan (opsional)</FieldLabel>
                <Input id="profile-position" autoComplete="organization-title" value={values.position} onChange={(event) => editProfile("position", event.target.value)} maxLength={100} disabled={busy} aria-invalid={Boolean(profileErrors.position)} aria-describedby={profileErrors.position ? "profile-position-error" : undefined} />
                {profileErrors.position && <FieldError id="profile-position-error">{profileErrors.position}</FieldError>}
              </Field>}
              {actor.role === "customer" && <Field data-disabled={busy} data-invalid={Boolean(profileErrors.address)}>
                <FieldLabel htmlFor="profile-address">Alamat pengiriman bawaan (opsional)</FieldLabel>
                <Textarea id="profile-address" autoComplete="street-address" value={values.address} onChange={event => editProfile("address", event.target.value)} maxLength={500} disabled={busy} aria-invalid={Boolean(profileErrors.address)} aria-describedby={`profile-address-help${profileErrors.address ? " profile-address-error" : ""}`}/>
                <FieldDescription id="profile-address-help">Dipakai untuk pesanan baru. Alamat pada pesanan sebelumnya tetap tersimpan.</FieldDescription>
                {profileErrors.address && <FieldError id="profile-address-error">{profileErrors.address}</FieldError>}
              </Field>}
            </FieldGroup>
            <div className="profile-form-actions">
              <Button disabled={busy || !profileDirty} type="submit"><Save data-icon="inline-start" aria-hidden="true" />{pending === "profile" ? "Menyimpan…" : "Simpan perubahan"}</Button>
              {profileDirty && <Button variant="ghost" type="button" disabled={busy} onClick={() => discardProfile()}>Batalkan perubahan</Button>}
            </div>
            <p className="profile-save-state" role="status">{profileDirty ? "Ada perubahan yang belum disimpan." : savedMessage}</p>
          </form>
        </section>
        <section className="panel profile-section" aria-labelledby="profile-account-title">
          <header className="panel-title profile-section-header"><div><h2 id="profile-account-title">Detail akun</h2><p>Hubungi administrator untuk mengubah informasi ini.</p></div></header>
          <dl className="profile-account-details">
            <div className="profile-account-item"><dt>Email akun</dt><dd>{actor.email}</dd></div>
            <div className="profile-account-item"><dt>{actor.role === "customer" ? "Jenis akun" : "Divisi / unit"}</dt><dd>{actor.role === "customer" ? "Pelanggan Unit Toko" : s.divisions.find((division) => division.id === actor.divisionId)?.name || "Unit Toko & Koperasi"}</dd></div>
            <div className="profile-account-item"><dt>Hak akses</dt><dd>{ROLE_LABELS[actor.role]}</dd></div>
          </dl>
        </section>
        <section className="panel profile-section" aria-labelledby="profile-password-title">
          <header className="panel-title profile-section-header"><div><h2 id="profile-password-title">Ubah kata sandi</h2><p>Setelah perubahan, Anda perlu masuk kembali di semua perangkat.</p></div></header>
          <form className="profile-form" onSubmit={changePassword} aria-busy={pending === "password"} noValidate>
            {passwordError && <Alert id="profile-password-error" variant="destructive" tabIndex={-1}><AlertDescription>{passwordError}</AlertDescription></Alert>}
            <FieldGroup>
              <Field data-disabled={busy} data-invalid={Boolean(passwordErrors.current)}>
                <FieldLabel htmlFor="old-password">Kata sandi saat ini</FieldLabel>
                <div className="profile-password-control"><Input id="old-password" type={visiblePasswords.current ? "text" : "password"} autoComplete="current-password" value={passwords.current} onChange={(event) => editPassword("current", event.target.value)} required disabled={busy} aria-invalid={Boolean(passwordErrors.current)} aria-describedby={passwordErrors.current ? "old-password-error" : undefined} />{passwordToggle("current", "kata sandi saat ini")}</div>
                {passwordErrors.current && <FieldError id="old-password-error">{passwordErrors.current}</FieldError>}
              </Field>
              <FieldGroup className="profile-fields">
                <Field data-disabled={busy} data-invalid={Boolean(passwordErrors.password)}>
                  <FieldLabel htmlFor="new-password">Kata sandi baru</FieldLabel>
                  <div className="profile-password-control"><Input id="new-password" type={visiblePasswords.password ? "text" : "password"} minLength={12} maxLength={128} autoComplete="new-password" value={passwords.password} onChange={(event) => editPassword("password", event.target.value)} required disabled={busy} aria-invalid={Boolean(passwordErrors.password)} aria-describedby={"new-password-help" + (passwordErrors.password ? " new-password-error" : "")} />{passwordToggle("password", "kata sandi baru")}</div>
                  <FieldDescription id="new-password-help">Gunakan 12–128 karakter.</FieldDescription>
                  {passwordErrors.password && <FieldError id="new-password-error">{passwordErrors.password}</FieldError>}
                </Field>
                <Field data-disabled={busy} data-invalid={Boolean(passwordErrors.confirm)}>
                  <FieldLabel htmlFor="confirm-password">Ulangi kata sandi baru</FieldLabel>
                  <div className="profile-password-control"><Input id="confirm-password" type={visiblePasswords.confirm ? "text" : "password"} autoComplete="new-password" value={passwords.confirm} onChange={(event) => editPassword("confirm", event.target.value)} required disabled={busy} aria-invalid={Boolean(passwordErrors.confirm)} aria-describedby={passwordErrors.confirm ? "confirm-password-error" : undefined} />{passwordToggle("confirm", "ulangan kata sandi baru")}</div>
                  {passwordErrors.confirm && <FieldError id="confirm-password-error">{passwordErrors.confirm}</FieldError>}
                </Field>
              </FieldGroup>
            </FieldGroup>
            <div className="profile-form-actions">
              <Button variant="outline" type="submit" disabled={busy || !passwordDirty}>{pending === "password" ? "Memperbarui…" : "Ubah kata sandi"}</Button>
              {passwordDirty && <Button variant="ghost" type="button" disabled={busy} onClick={() => { setPasswords(EMPTY_PASSWORDS); setPasswordErrors({}); setPasswordError(""); }}>Kosongkan isian</Button>}
            </div>
          </form>
        </section>
      </div>
    </div>
  );
}
