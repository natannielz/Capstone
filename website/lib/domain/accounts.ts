export const ROLES = ["pic", "kepala", "staf", "kurir", "laporan", "penagihan", "pimpinan", "akuntansi", "admin", "customer"] as const;
export type Role = typeof ROLES[number];
export const isRole = (value: unknown): value is Role => typeof value === "string" && (ROLES as readonly string[]).includes(value);
export type Actor = { id: string; name: string; role: Role; divisionId: string | null; active: boolean; email?:string; avatar?:string; phone?:string; position?:string; address?:string };
export const DEMO_ACCOUNTS: (Actor & { label: string; detail: string; group: string })[] = [
  { id:"pic-a", name:"Nadia Putri", role:"pic", label:"PIC Divisi", detail:"Divisi Operasional", divisionId:"div-ops", group:"Divisi BNI", active:true },
  { id:"pic-b", name:"Raka Pratama", role:"pic", label:"PIC Divisi", detail:"Divisi Teknologi", divisionId:"div-ti", group:"Divisi BNI", active:true },
  { id:"kepala", name:"Dewi Lestari", role:"kepala", label:"Kepala Toko", detail:"Pengawasan operasional", divisionId:null, group:"Unit Toko", active:true },
  { id:"staf", name:"Arif Setiawan", role:"staf", label:"Staf Toko", detail:"Pesanan & persediaan", divisionId:null, group:"Unit Toko", active:true },
  { id:"kurir", name:"Bima Saputra", role:"kurir", label:"Kurir", detail:"Pengantaran divisi", divisionId:null, group:"Unit Toko", active:true },
  { id:"laporan", name:"Maya Sari", role:"laporan", label:"Pengolah Laporan", detail:"Rekap transaksi", divisionId:null, group:"Koperasi", active:true },
  { id:"penagihan", name:"Sinta Ayu", role:"penagihan", label:"Bagian Penagihan", detail:"Invoice & pembayaran", divisionId:null, group:"Koperasi", active:true },
  { id:"pimpinan", name:"Hendra Wijaya", role:"pimpinan", label:"Pimpinan Unit", detail:"Persetujuan laporan", divisionId:null, group:"Koperasi", active:true },
  { id:"akuntansi", name:"Laras Wulandari", role:"akuntansi", label:"Akuntansi", detail:"Jurnal & tutup periode", divisionId:null, group:"Koperasi", active:true },
  { id:"admin", name:"Admin Demo", role:"admin", label:"Administrator", detail:"Akun & pengaturan", divisionId:null, group:"Administrasi", active:true },
  { id:"customer-demo", name:"Pelanggan Demo", role:"customer", label:"Pelanggan", detail:"Belanja pribadi", divisionId:null, group:"Pelanggan", active:true, email:"customer@unit-toko.demo", address:"" },
];
export const ROLE_LABELS = Object.fromEntries(DEMO_ACCOUNTS.map(a=>[a.role,a.label])) as Record<Role,string>;
