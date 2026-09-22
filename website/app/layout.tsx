import type { Metadata } from "next";
import localFont from "next/font/local";
import "./globals.css";
import "./polish.css";
import "./orders-v5.css";
import "./catalog-v5.css";
import "./accessibility-v5.css";
import "./operations-v6.css";
import "./transactions-v6.css";
import "./storefront-v7.css";
import "./account-v7.css";

const bodyFont = localFont({src:"../public/fonts/dm-sans-variable.ttf",variable:"--font-body",display:"swap",weight:"100 1000"});
const displayFont = localFont({src:"../public/fonts/archivo-black.ttf",variable:"--font-display",display:"swap",weight:"400"});

export const metadata: Metadata = {
  metadataBase: new URL(process.env.NEXT_PUBLIC_APP_URL || (process.env.VERCEL_URL ? `https://${process.env.VERCEL_URL}` : "http://localhost:3000")),
  title: "Unit Toko | Pantry, Rapat & Merchandise",
  description: "Belanja kebutuhan pantry, rapat, dan merchandise. Pantau pesanan dan pengiriman di Unit Toko. Demo capstone dengan data simulasi.",
  openGraph: {title:"Unit Toko | Untuk meja kerja. Untuk seluruh divisi.", description:"Pantry, rapat, dan merchandise. Siapkan kebutuhan tim dalam satu portal. Demo capstone.",images:["/images/og-v3.png"],locale:"id_ID",type:"website"},
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
    <html lang="id" data-scroll-behavior="smooth" className={`${bodyFont.variable} ${displayFont.variable}`}>
      <body className="antialiased">{children}</body>
    </html>
  );
}
