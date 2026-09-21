"use client";

import { useEffect, useState, type FormEvent } from "react";
import { Camera, Save } from "lucide-react";
import { toast } from "sonner";
import { Art } from "./art";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Field, FieldDescription, FieldError, FieldGroup, FieldLabel } from "@/components/ui/field";
import { ROLE_LABELS } from "@/lib/domain/accounts";
import type { WorkspaceContext } from "./workspace";

type ProfileValues = { name: string; phone: string; position: string };
type PasswordValues = { current: string; password: string; confirm: string };
type Errors<T> = Partial<Record<keyof T, string>>;
type ProfileProps = WorkspaceContext & { onDirtyChange?: (dirty: boolean) => void };

const EMPTY_PASSWORDS: PasswordValues = { current: "", password: "", confirm: "" };
const PASSWORD_FIELDS = { current: "old-password", password: "new-password", confirm: "confirm-password" };

function focusField(id: string) {
  requestAnimationFrame(() => document.getElementById(id)?.focus());
}

function errorMessage(error: unknown) {
  return error instanceof Error ? error.message : "Permintaan belum berhasil. Coba kembali.";
}

async function request(url: string, init: RequestInit) {
  let response: Response;
  try {
    response = await fetch(url, init);
  } catch {
    throw new Error("Koneksi bermasalah. Periksa jaringan, lalu coba kembali.");
  }
  const data = await response.json().catch(() => null) as { error?: string; message?: string } | null;
  if (!response.ok) throw new Error(data?.error || "Permintaan belum berhasil. Coba kembali.");
  return data;
}

export function Profile({ actor, s, refresh, onDirtyChange }: ProfileProps) {
  const [values, setValues] = useState<ProfileValues>({
    name: actor.name,
    phone: actor.phone || "",
    position: actor.position || "",
  });
  const [savedValues, setSavedValues] = useState(values);
  const [passwords, setPasswords] = useState<PasswordValues>(EMPTY_PASSWORDS);
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

  useEffect(() => {
    onDirtyChange?.(dirty);
  }, [dirty, onDirtyChange]);

  useEffect(() => () => onDirtyChange?.(false), [onDirtyChange]);

  function editProfile(key: keyof ProfileValues, value: string) {
    setValues((previous) => ({ ...previous, [key]: value }));
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
    };
    const errors: Errors<ProfileValues> = {};
    if (!next.name) errors.name = "Masukkan nama lengkap.";
    else if (next.name.length > 100) errors.name = "Nama lengkap maksimal 100 karakter.";
    if (next.phone.length > 30) errors.phone = "Nomor kontak maksimal 30 karakter.";
    if (next.position.length > 100) errors.position = "Jabatan maksimal 100 karakter.";
    setProfileErrors(errors);
    setSaveError("");
    setSavedMessage("");
    const invalid = Object.keys(errors)[0];
    if (invalid) {
      focusField("profile-" + invalid);
      return;
    }
    setPending("profile");
    try {
      await request("/api/profile", {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(next),
      });
      setValues(next);
      setSavedValues(next);
      setSavedMessage("Perubahan profil telah disimpan.");
      await refresh();
    } catch (error) {
      setSaveError(errorMessage(error));
      focusField("profile-save-error");
    } finally {
      setPending(null);
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
      onDirtyChange?.(false);
      window.location.assign("/login");
    } catch (error) {
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
    <div className="profile-layout">
      <aside className="panel profile-card">
        <Art className="profile-photo" src={actor.avatar || "/images/avatars/" + (actor.role === "pic" ? "pic-a" : actor.role) + ".png"} alt={"Foto profil " + actor.name} />
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
        <section className="panel profile-section" aria-labelledby="profile-personal-title">
          <header className="panel-title profile-section-header"><div><h2 id="profile-personal-title">Data pribadi</h2><p>Perbarui nama dan informasi kontak.</p></div></header>
          <form className="profile-form" onSubmit={save} aria-busy={pending === "profile"} noValidate>
            {saveError && <Alert id="profile-save-error" variant="destructive" tabIndex={-1}><AlertDescription>{saveError}</AlertDescription></Alert>}
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
              <Field data-disabled={busy} data-invalid={Boolean(profileErrors.position)}>
                <FieldLabel htmlFor="profile-position">Jabatan (opsional)</FieldLabel>
                <Input id="profile-position" autoComplete="organization-title" value={values.position} onChange={(event) => editProfile("position", event.target.value)} maxLength={100} disabled={busy} aria-invalid={Boolean(profileErrors.position)} aria-describedby={profileErrors.position ? "profile-position-error" : undefined} />
                {profileErrors.position && <FieldError id="profile-position-error">{profileErrors.position}</FieldError>}
              </Field>
            </FieldGroup>
            <div className="profile-form-actions">
              <Button disabled={busy || !profileDirty} type="submit"><Save data-icon="inline-start" aria-hidden="true" />{pending === "profile" ? "Menyimpan…" : "Simpan perubahan"}</Button>
              {profileDirty && <Button variant="ghost" type="button" disabled={busy} onClick={() => { setValues(savedValues); setProfileErrors({}); setSaveError(""); setSavedMessage(""); }}>Batalkan perubahan</Button>}
            </div>
            <p className="profile-save-state" role="status">{profileDirty ? "Ada perubahan yang belum disimpan." : savedMessage}</p>
          </form>
        </section>
        <section className="panel profile-section" aria-labelledby="profile-account-title">
          <header className="panel-title profile-section-header"><div><h2 id="profile-account-title">Detail akun</h2><p>Hubungi administrator untuk mengubah informasi ini.</p></div></header>
          <dl className="profile-account-details">
            <div className="profile-account-item"><dt>Email akun</dt><dd>{actor.email}</dd></div>
            <div className="profile-account-item"><dt>Divisi / unit</dt><dd>{s.divisions.find((division) => division.id === actor.divisionId)?.name || "Unit Toko & Koperasi"}</dd></div>
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
                <Input id="old-password" type="password" autoComplete="current-password" value={passwords.current} onChange={(event) => editPassword("current", event.target.value)} required disabled={busy} aria-invalid={Boolean(passwordErrors.current)} aria-describedby={passwordErrors.current ? "old-password-error" : undefined} />
                {passwordErrors.current && <FieldError id="old-password-error">{passwordErrors.current}</FieldError>}
              </Field>
              <FieldGroup className="profile-fields">
                <Field data-disabled={busy} data-invalid={Boolean(passwordErrors.password)}>
                  <FieldLabel htmlFor="new-password">Kata sandi baru</FieldLabel>
                  <Input id="new-password" type="password" minLength={12} maxLength={128} autoComplete="new-password" value={passwords.password} onChange={(event) => editPassword("password", event.target.value)} required disabled={busy} aria-invalid={Boolean(passwordErrors.password)} aria-describedby={"new-password-help" + (passwordErrors.password ? " new-password-error" : "")} />
                  <FieldDescription id="new-password-help">Gunakan 12–128 karakter.</FieldDescription>
                  {passwordErrors.password && <FieldError id="new-password-error">{passwordErrors.password}</FieldError>}
                </Field>
                <Field data-disabled={busy} data-invalid={Boolean(passwordErrors.confirm)}>
                  <FieldLabel htmlFor="confirm-password">Ulangi kata sandi baru</FieldLabel>
                  <Input id="confirm-password" type="password" autoComplete="new-password" value={passwords.confirm} onChange={(event) => editPassword("confirm", event.target.value)} required disabled={busy} aria-invalid={Boolean(passwordErrors.confirm)} aria-describedby={passwordErrors.confirm ? "confirm-password-error" : undefined} />
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
