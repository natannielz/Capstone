import type { Metadata } from "next";
import localFont from "next/font/local";
import "./globals.css";
import { isPilotRelease } from "../lib/release-scope";
import { SkipLink } from "../components/workspace-kit/shell-links";

const bodyFont = localFont({src:"../../public/fonts/dm-sans-variable.ttf",variable:"--font-body",display:"swap",weight:"100 1000"});
const displayFont = localFont({src:"../../public/fonts/archivo-black.ttf",variable:"--font-display",display:"swap",weight:"400"});
// Package numerals and carton print on the landing page only; not preloaded on other pages.
const stencilFont = localFont({src:"../../public/fonts/saira-stencil-one.woff2",variable:"--font-stencil",display:"swap",weight:"400",preload:false});

export const metadata: Metadata = {
  metadataBase: new URL(process.env.NEXT_PUBLIC_APP_URL || (process.env.VERCEL_URL ? `https://${process.env.VERCEL_URL}` : "http://localhost:3000")),
  title: "Unit Toko | Katalog dan Permintaan Cabang",
  description: "Katalog kemasan dan permintaan kebutuhan untuk cabang BNI serta akun usaha yang telah disetujui.",
  openGraph: {title:"Unit Toko | Kebutuhan cabang dan organisasi", description:"Pilih kemasan, ajukan kebutuhan cabang, dan ikuti peninjauan toko melalui akun organisasi Anda.",images:["/images/og-v3.png"],locale:"id_ID",type:"website"},
  icons: {
    icon: "/favicon.svg",
    shortcut: "/favicon.svg",
  },
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="id" data-scroll-behavior="smooth" className={`${bodyFont.variable} ${displayFont.variable} ${stencilFont.variable}`}>
      <body className="antialiased">
        <SkipLink />
        {isPilotRelease && process.env.VERCEL && !process.env.UNIT_TOKO_API_ORIGIN && (
          <aside role="status" style={{ background: "#fff3d4", color: "#513d12", padding: "16px 24px", borderBottom: "1px solid #dbc48f", lineHeight: 1.6 }}>
            <strong>Pratinjau Release 1 — layanan daring belum diaktifkan.</strong>{" "}
            Masuk, katalog, dan pengajuan daring menunggu koneksi layanan serta persetujuan data. Pilot lokal tersedia melalui pengelola.
          </aside>
        )}
        {children}
        {process.env.NODE_ENV === "development" && process.env.FIGMA_CAPTURE === "1" && (
          <script src="https://mcp.figma.com/mcp/html-to-design/capture.js" async />
        )}
      </body>
    </html>
  );
}
