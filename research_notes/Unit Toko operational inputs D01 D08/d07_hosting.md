# D07 — Free / near-free hosting for Unit Toko (ASP.NET Core .NET 10 API + worker, MongoDB 8 replica set, Auth0 MFA, Next.js on Vercel)

Research date: 2026-10-16. Prices USD unless stated. IDR conversions use an assumed ~Rp16,500/USD (NOT verified against a live rate in this research — recompute before quoting). Azure prices were pulled live from the Azure Retail Prices API (prices.azure.com) for region `indonesiacentral` on 2026-10-16; monthly = hourly x 730.

## Q1. Free tiers / pricing and limits per provider (2026)

### Takeaway
Nothing is "free and production-safe" for this stack. The only free compute that can realistically host the whole stack (API + worker + self-hosted Mongo replica set + persistent disk + Tesseract/ffmpeg) is Oracle Cloud Always Free, and it was quietly cut to 2 OCPU / 12 GB in mid-2026, has no Jakarta region (nearest: Batam, Indonesia North), and requires a Linux/ARM port. Managed MongoDB is free only via Atlas M0 (512 MB, no backups). Auth0 Free (25,000 MAU) does NOT list MFA; MFA ("Pro MFA") starts at Essentials $35/month.

### Cited Findings

**MongoDB Atlas**
- M0 Free is a 3-node replica set; replication factor fixed at 3 and not modifiable — [Atlas Free Cluster Limits](https://www.mongodb.com/docs/atlas/reference/free-shared-limitations/) (via search summary of the official page). Because it is a replica set, multi-document transactions are possible in principle; I found no M0-specific transaction prohibition, but did not find an explicit "transactions supported on M0" statement either (see Gaps).
- M0 storage max 0.5 GB (uncompressed BSON + indexes); data transfer limit 10 GB in + 10 GB out per rolling 7 days, throttled when exceeded; no server-side JavaScript ($where, map-reduce) — [Atlas Free Cluster Limits](https://www.mongodb.com/docs/atlas/reference/free-shared-limitations/)
- M0: 512 MB storage, 32 MB sort memory, up to 100 operations/second — [MongoDB Pricing FAQ](https://www.mongodb.com/pricing)
- M0 runs MongoDB 8.0, version cannot be upgraded; one free cluster per project; cannot configure memory/storage; cannot enable backups (docs suggest mongodump/mongorestore); cannot run failover tests — [Atlas Free Cluster Limits](https://www.mongodb.com/docs/atlas/reference/free-shared-limitations/)
- Idle free clusters can be paused; docs advise exporting data if not using the cluster for an extended period, and a paused cluster on an old version may not be restorable — [Atlas Free Cluster Limits](https://www.mongodb.com/docs/atlas/reference/free-shared-limitations/)
- "Backup services are not available for the Free tier"; Free and Flex not charged for outgoing data — [MongoDB Pricing FAQ](https://www.mongodb.com/pricing)
- Atlas Flex: from $8/month base, 5 GB storage, 100 ops/s, burst to 500 ops/s at extra charge, capped at $30/month; billed hourly (~$0.011/h base) — [MongoDB help: Understanding Atlas Flex](https://intercom-help.mongodb.com/en/articles/10119149-understanding-atlas-flex-tier); [Now GA: Atlas Flex](https://www.mongodb.com/products/updates/now-ga-mongodb-atlas-flex-tier/)
- Flex reportedly absorbed legacy M2/M5 and Serverless — [Amnic (third-party)](https://amnic.com/blogs/mongodb-atlas-pricing-tiers-real-costs-and-how-to-control-them)
- Dedicated M10: 2 GB RAM, 2 vCPU, 10–128 GB storage, base $0.08/hr (~$58/month, ~Rp960k) — region/provider varies — [MongoDB Pricing](https://www.mongodb.com/pricing)

**Auth0**
- Free: $0, no credit card, up to 25,000 MAU; includes 1 custom domain (credit-card verification required for custom domains), passwordless, unlimited social connections, 5 Organizations, basic attack protection, 1 enterprise connection, self-service SSO, SCIM, community support — [Auth0 Pricing](https://auth0.com/pricing)
- Plan comparison table: "Pro MFA Factors — Free: Not included; Essentials and above: Included"; "Enterprise MFA Factors" only on Professional/Enterprise; Role Management not included on Free — [Auth0 Pricing](https://auth0.com/pricing)
- Essentials $35/month (B2C, 500 MAU tier) includes "Pro Multi-Factor Authentication" and higher limits — [Auth0 Pricing](https://auth0.com/pricing)
- "Available factors are dependent on your subscription plan" — [Auth0 MFA Factors docs](https://auth0.com/docs/secure/multi-factor-authentication/multi-factor-authentication-factors)
- Community moderator: on Free, paid MFA factors can be enabled without charge "for testing purposes" but continued use without upgrading can get the tenant blocked — [Auth0 Community thread](https://community.auth0.com/t/clarification-on-pro-mfa-warning-while-using-totp-in-free-plan/189505)
- Third-party sources conflict on Free plan MFA and custom domain — [costbench](https://www.costbench.com/software/identity-access-management/auth0/free-plan/), [toolradar](https://toolradar.com/tools/auth0/pricing), [supertokens](https://supertokens.com/blog/auth0-pricing-the-complete-guide). The official pricing page is authoritative.

**Azure (live retail API, Indonesia Central = Jakarta)**
- App Service Basic B1 Windows: $0.075/h (~$54.75/month, ~Rp903k); B1 Linux: $0.017/h (~$12.41/month, ~Rp205k); B2 Windows $0.15/h; S1 Windows $0.117/h; P0v3 Windows $0.1575/h, Linux $0.08235/h — [Azure Retail Prices API, indonesiacentral](https://prices.azure.com/api/retail/prices)
- F1 Free listed in the Indonesia Central price sheet for both Windows and Linux (API shows a nominal $0.01/h meter for "F1 App" — F1 is documented elsewhere as free; treat this meter as an anomaly to verify in the pricing calculator) — [Azure Retail Prices API](https://prices.azure.com/api/retail/prices)
- F1 limits: 60 CPU-minutes/day, 1 GB storage, no custom-domain SSL, no Always On — [cloudwebschool](https://cloudwebschool.com/docs/azure/fundamentals/azure-free-tier/), [techhub.iodigital](https://techhub.iodigital.com/articles/az-204-cheatsheets/Develop-Azure-Compute-Solutions/Azure-App-Service); Free/Shared plans limited to 500 MB local storage per app vs 11 GB on B1 — [Azure docs (China mirror)](https://docs.azure.cn/en-us/app-service/operating-system-functionality)
- F1 quota errors ("quota of 0 instances") reported on Free Trial subscriptions — [Microsoft Q&A](https://learn.microsoft.com/en-za/answers/questions/2087541/quota-limitations-for-appservice-plans-on-free-tri)
- VMs in Indonesia Central (hourly, Linux / Windows): B1s $0.0119 / $0.0159; B1ms $0.0238 / $0.0278; B2ats v2 (2 vCPU, 1 GB) $0.0107 / $0.0199; B2als v2 (2 vCPU, 4 GB) $0.0428 / $0.052; B2s (2 vCPU, 4 GB) $0.0475 / $0.0555 — [Azure Retail Prices API](https://prices.azure.com/api/retail/prices). Monthly: B2als v2 Linux ~$31, Windows ~$38; B2s Windows ~$40.5 (disks extra).
- Azure for Students: USD 100 credit for 12 months, no credit card, full-time university students only, renewable annually while a student; must convert to Pay-As-You-Go afterwards — [Azure for Students](https://azure.microsoft.com/en-ca/free/students/), [HKU IT guide](https://its.hku.hk/kb/microsoft-azure-for-students)

**AWS**
- New Free Tier (credits model): $100 credits at sign-up + up to $100 more earned = up to $200 over 6 months; Free-plan account "closes on its own 6 months after you open it or when your credits run out, whichever comes first"; Free plan limited to select services; must convert to Paid plan to continue — [AWS Free Tier](https://aws.amazon.com/free/)

**Google Cloud**
- Always-free: 1 non-preemptible e2-micro VM/month only in us-west1, us-central1, us-east1; 30 GB-months standard persistent disk; 1 GB egress from North America; Cloud Storage free 5 GB only in US regions — [GCP Free features](https://cloud.google.com/free/docs/free-cloud-features)

**Oracle Cloud (OCI) Always Free**
- A1 (Arm): first 1,500 OCPU-hours and 9,000 GB-hours/month free, "For Always Free tenancies, this is equivalent to 2 OCPUs and 12 GB of memory"; up to two A1 instances totaling 2 OCPU; plus up to two VM.Standard.E2.1.Micro AMD (1/8 OCPU, 1 GB RAM, 50 Mbps internet) — [OCI Always Free Resources](https://docs.oracle.com/en-us/iaas/Content/FreeTier/resourceref.htm)
- CHANGED 2026: allowance cut from 4 OCPU/24 GB to 2 OCPU/12 GB, effective ~15 June 2026, without announcement; users found instances shut down — [InfoQ, Jul 2026](https://infoq.com/news/2026/07/oracle-cloud-free-tier-limits/). Pay-As-You-Go accounts may still get 4/24 free (unconfirmed, based on support emails/Reddit) — [fullmetalbrackets](https://fullmetalbrackets.com/blog/oci-free-tier-breakdown)
- 200 GB total block volume (boot + block combined) + 5 volume backups; must be in home region; min boot volume 47 GB — [OCI Always Free Resources](https://docs.oracle.com/en-us/iaas/Content/FreeTier/resourceref.htm)
- Object Storage: 20 GB combined (Standard/IA/Archive) + 50,000 API requests/month for Always-Free-only accounts (paid/trial: 10 GB each tier) — [OCI Always Free Resources](https://docs.oracle.com/en-us/iaas/Content/FreeTier/resourceref.htm)
- 10 TB/month outbound data free — [OCI Always Free Resources](https://docs.oracle.com/en-us/iaas/Content/FreeTier/resourceref.htm)
- Always-Free images are Linux (Oracle Linux, Ubuntu etc.); Windows images are not Always-Free eligible — [OCI Always Free Resources](https://docs.oracle.com/en-us/iaas/Content/FreeTier/resourceref.htm) (images list); A1 is Arm, so any Windows x64 build cannot run there regardless.
- Regions: OCI has Indonesia North (Batam) `ap-batam-1` and Singapore `ap-singapore-1`/`ap-singapore-2`; no Jakarta region listed — [OCI Regions](https://docs.oracle.com/en-us/iaas/Content/General/Concepts/regions.htm)

**PaaS (Render / Fly.io / Railway / Koyeb / Northflank)**
- Render Free web service spins down after 15 min without inbound traffic, ~1 minute to spin up; ephemeral filesystem lost on redeploy/restart/spin-down; Free services cannot attach persistent disks; free Render Postgres expires after 30 days; Render says do not use free instances for production — [Render Free docs](https://render.com/docs/free)
- Fly.io: no monthly platform fee and no free allowance quoted for compute; smallest always-on app $2.19/30 days; shared-cpu-1x 512 MB always-on $3.69/month; 10 GB volume $1.50/month ($0.15/GB); 10 GB free snapshot allowance; prices quoted for iad/ewr, other regions differ — [Fly.io Pricing](https://fly.io/docs/about/pricing/)
- Railway: Trial = one-time $5 credit; "Free" plan = $1 free credit/month; Hobby/Pro paid — [Railway Plans](https://docs.railway.com/reference/pricing/plans)
- Koyeb: now "joining Mistral AI"; current pricing page is AI/GPU-oriented with scale-to-zero; I could not confirm a current free CPU instance — [Koyeb Pricing](https://www.koyeb.com/pricing)
- Northflank Developer Sandbox: always-on (no sleeping), 2 free services, 1 free database addon, 2 free cron jobs; region list on pricing page: US West/Central/East, EU West, Asia East (no Jakarta/Singapore listed); CPU $0.01667/vCPU/hour on PAYG — [Northflank Pricing](https://northflank.com/pricing)

**Indonesian providers (IDR, Indonesian DCs)**
- Biznet Gio NEO Lite: XS 1.1 (1 core, 1 GB, 60 GB) Rp50.000; SS 2.1 Rp75.000; SS 2.2 Rp100.000; MS 4.2 Rp125.000; MS 4.4 Rp165.000 (billing period shown without explicit "/month"; excl. PPN). Biznet Gio sells a separate Windows VPS range with Windows Server license included (prices not captured) — [Biznet Gio Pricelist](https://biznetgio.com/pricelist), [NEO Lite](https://www.biznetgio.com/en/product/neo-lite)
- IDCloudHost (third-party listings, verify on vendor site): VPS Entry Rp40.000/month (2 core, 1 GB, 25 GB, Indonesia); Rocket VPS 1 Rp105.000/month (2 core, 4 GB, 60 GB, Linux/Windows); eXtreme 1 Rp112.000/month (Linux/Windows); Cloud 2 Indonesia Rp120.000/month (1 vCPU, 4 GB, 50 GB NVMe, excl. tax); Rocket VPS 4 Rp245.000/month; DCs Indonesia and Singapore — [whtop IDCloudHost listings](https://www.whtop.com/id/plans/idcloudhost.com/148727), [Rocket VPS 1](https://www.whtop.com/id/plans/idcloudhost.com/148742), [eXtreme 1](https://www.whtop.com/id/plans/idcloudhost.com/148739)
- Hostinger KVM 1 (1 vCPU, 4 GB, 50 GB, 4 TB transfer): $19.49 monthly or $12.99/month on 1-year term per a May 2026 benchmark; promo "from $4.99" with higher renewal; Hostinger lists Indonesia as a VPS location — [vpsbenchmarks](https://www.vpsbenchmarks.com/trials/hostinger_performance_trial_28May2026), [hostscore](https://hostscore.net/choose/hostinger-vps-alternatives/)

### Inferences
- Windows license premium on Azure Indonesia Central ≈ $0.0092/h on B2ats v2 (~$6.7/month) and ≈ $0.007–0.008/h on B2s/B2als v2 (~$5–7/month); App Service Windows B1 is ~4.4x Linux B1 ($54.75 vs $12.41). Keeping Windows costs ~$40–55/month minimum in Jakarta.
- Auth0 Free cannot satisfy "MFA required" in a supported way; cheapest compliant path is Essentials $35/month (~Rp578k) — this one line item likely exceeds the entire compute budget of the Linux options. Alternative: verify whether passkeys/passwordless on Free satisfy the project's MFA requirement (probably not, as it's single-factor), or evaluate self-hosted/other IdPs (out of scope here).
- Atlas M0 0.5 GB is likely enough for a pilot of a small cooperative shop's transactional data (not files), but no backups + 100 ops/s + 10 GB/week transfer make it pilot-only. Flex ($8–30) also lacks dedicated-tier continuous backup (Flex backup details not verified — Gap).

### Gaps
- No official statement found explicitly confirming multi-document transactions on M0/Flex (replica set architecture implies support; verify with a test transaction).
- Whether Atlas M0/Flex can be deployed in AWS Jakarta (ap-southeast-3) — M0 region list not retrieved; Singapore is commonly available.
- Atlas M0 idle-pause threshold (days) not captured from the page.
- Atlas Flex backup policy not verified.
- Exact Auth0 Free MFA factor list (e.g., whether email/WebAuthn-as-second-factor is permitted) not on official page; only "Pro MFA Factors: Not included".
- Windows license add-on prices at Biznet Gio / IDCloudHost not found.
- Live USD→IDR rate not verified.
- Koyeb current free tier not confirmed.

## Q2. Which options can run the workload (worker, Mongo replica set, persistent disk, Tesseract/ffmpeg; sleep behavior)

### Takeaway
Only a full VM (Oracle A1, Azure VM, Indonesian VPS, Hostinger, GCP e2-micro) runs everything in one place. PaaS free tiers (Render, Azure F1, Railway Free) fail on always-on worker and/or persistent disk. All free VM options except Azure/Indonesian VPS require Linux.

### Cited Findings
- Render Free: sleeps after 15 min, no persistent disk, ephemeral FS — cannot hold evidence files or a DB — [Render Free docs](https://render.com/docs/free)
- Azure F1: 60 CPU-min/day, 1 GB, Free/Shared 500 MB local storage — too small for worker + OCR/ffmpeg — [cloudwebschool](https://cloudwebschool.com/docs/azure/fundamentals/azure-free-tier/), [docs.azure.cn](https://docs.azure.cn/en-us/app-service/operating-system-functionality)
- Fly.io: volumes $0.15/GB-month, always-on machines from $2.19–$3.69/month; Linux containers only — [Fly.io Pricing](https://fly.io/docs/about/pricing/)
- Northflank Sandbox: always-on, 2 services (API + worker) + 1 DB addon free, persistent volumes supported in feature table; regions not in SE Asia per pricing page — [Northflank Pricing](https://northflank.com/pricing)
- Oracle A1: 2 OCPU/12 GB, 200 GB block storage — enough RAM for API + worker + single-node mongod replica set + Tesseract; Arm64 Linux only — [OCI Always Free Resources](https://docs.oracle.com/en-us/iaas/Content/FreeTier/resourceref.htm)
- GCP e2-micro (shared 2 vCPU burst, 1 GB RAM) US-only, 30 GB disk — [GCP Free features](https://cloud.google.com/free/docs/free-cloud-features)

### Inferences
- A single-node MongoDB replica set (`--replSet rs0` + `rs.initiate()`) supports transactions and is the realistic self-hosted choice on one VM; a true 3-node set on one VM gives no HA. Oracle's 2 A1 instances could host 2 data nodes + an arbiter on a Micro, but this is fragile.
- 1 GB RAM machines (e2-micro, AMD Micro, IDCloudHost Entry, Biznet XS 1.1, Azure B2ats v2) are too small for .NET API + worker + mongod + Tesseract concurrently; target ≥4 GB RAM.
- .NET 10, Tesseract and ffmpeg all have Linux x64 and arm64 builds, so a Linux port is plausible; ARM (Oracle A1) adds a second axis (verify Python OCR helper wheels on arm64).
- Windows-capable options: Azure App Service Windows (B1 $54.75+, but App Service has no persistent NTFS disk beyond its content share and cannot host MongoDB), Azure Windows VM (~$38–41/month for 4 GB), Indonesian Windows VPS (IDCloudHost Rocket VPS 1 Rp105k lists Windows; license cost unclear).

### Gaps
- Fly.io Singapore (sin) per-region machine price not captured.
- Northflank volume pricing and free-tier persistence specifics not verified.

## Q3. Object storage for private files and MongoDB backup

### Takeaway
Cloudflare R2 (10 GB free, zero egress) or Oracle Object Storage (20 GB free) are the free off-host targets; nightly `mongodump --archive --gzip` (consistent with `--oplog` on a replica set) to object storage plus periodic restore tests is the backup approach for M0 and self-hosted Mongo alike.

### Cited Findings
- R2 free tier: 10 GB-month storage, 1M Class A, 10M Class B ops/month, free egress; free tier only Standard storage. Paid: $0.015/GB-month Standard, $0.01 IA; Class A $4.50/M, Class B $0.36/M; usage rounded up to next unit — [Cloudflare R2 Pricing](https://developers.cloudflare.com/r2/pricing/)
- OCI Object Storage 20 GB + 50k requests/month free; 5 free block-volume backups — [OCI Always Free Resources](https://docs.oracle.com/en-us/iaas/Content/FreeTier/resourceref.htm)
- GCP Cloud Storage free 5 GB only in US regions — [GCP Free features](https://cloud.google.com/free/docs/free-cloud-features)
- Atlas Free: no backups; use mongodump/mongorestore. Backups billed on paid tiers by snapshot/oplog storage — [Atlas Free Cluster Limits](https://www.mongodb.com/docs/atlas/reference/free-shared-limitations/), [MongoDB Pricing FAQ](https://www.mongodb.com/pricing)

### Inferences
- R2 data location: R2 supports jurisdiction/location hints, but data likely not in Indonesia — relevant to UU PDP (not verified here).
- Evidence files should be encrypted client-side (or at least server-side with app-held keys) before upload if stored outside Indonesia.
- Restore test: monthly `mongorestore` into a scratch replica set + checksum of a sample of evidence files; record time-to-restore.

### Gaps
- Backblaze B2 current free allowance not researched in this pass (historically first 10 GB free — unverified for 2026).
- R2 region/jurisdiction options for APAC not verified.

## Q4. TLS/DNS and domains

### Takeaway
TLS can be free (Let's Encrypt / Cloudflare free proxy). A `.co.id` requires company legal documents (NIB/NPWP/SK Kemenkumham + KTP); `.id` is cheaper and looser. Exact 2026 PANDI prices not found.

### Cited Findings
- .co.id is for legal entities operating in Indonesia; documents: NIB/NPWP/SK Kumham plus KTP-el/passport; trademark-linked names need trademark certificate — [PANDI policy proposal PDF (2025, draft)](https://pandi.id/public/files/2025/5/rumusan-usulan-pengembangan-kebijakan-layanan-proxy-nama-domain-hasil-rapat-pleno-fms-1746966280.pdf)
- PANDI keeps .co.id prices higher to preserve credibility because it requires legal documents — [Bisnis.com](https://teknologi.bisnis.com/read/20210322/84/1370783/makin-diminati-pengguna-nama-domain-id-sentuh-500000)
- Old suggested retail .id price Rp250.000 excl. VAT (2020, likely outdated) — [Bisnis.com 2020](https://teknologi.bisnis.com/read/20200826/84/1283469/tak-hanya-identitas-bangsa-domain-id-diyakini-berpotensi-besar)
- Registrars increased from 12 to 24 accredited; PANDI targets 2.8M .id names by 2031 — [Bisnis.com Sep 2026](https://teknologi.bisnis.com/read/20260901/84/2000571/pandi-targetkan-domain-id-tembus-28-juta-pada-2031)
- Azure App Service free managed certificates require Basic or higher (not F1) — [techhub.iodigital](https://techhub.iodigital.com/articles/az-204-cheatsheets/Develop-Azure-Compute-Solutions/Azure-App-Service)
- Auth0 custom domain on Free requires credit-card verification — [Auth0 Pricing](https://auth0.com/pricing)

### Inferences
- Cheapest: use a subdomain of an existing domain or `.my.id`/`.biz.id` (cheap personal/SME .id SLDs) with Cloudflare free DNS; a cooperative (koperasi) with NIB could register `.co.id` or `.or.id`.
- Vercel provides free TLS for the frontend; backend on VM can use Caddy/Let's Encrypt.

### Gaps
- Current 2026 PANDI/registrar prices for .id, .co.id, .my.id not found from an official source.
- Cloudflare free plan terms not fetched (well-known: free DNS + proxy + universal SSL; unverified in this pass).

## Q5. Risks of free tiers and UU PDP cross-border considerations

### Takeaway
Free tiers carry no SLA, can change without notice (Oracle did in June 2026), can reclaim idle resources, and some expire (AWS 6 months, Render Postgres 30 days, Azure Students 12 months). UU PDP Art. 56 allows cross-border transfer with equivalent protection / binding safeguards / consent; hosting in Jakarta avoids the issue, Singapore/US requires documented safeguards.

### Cited Findings
- Oracle idle reclamation: instance idle if over 7 days CPU p95 < 20%, network < 20%, memory < 20% (A1 only) — [OCI Always Free Resources](https://docs.oracle.com/en-us/iaas/Content/FreeTier/resourceref.htm). (A secondary source cites 15% — [braindetox](https://braindetox.kr/en/posts/oracle_always_free_backend_2026.html); official page says 20%.)
- Oracle cut A1 free allowance by half in June 2026 without notification; instances were shut down — [InfoQ](https://infoq.com/news/2026/07/oracle-cloud-free-tier-limits/)
- AWS Free plan account auto-closes at 6 months or when credits run out — [AWS Free Tier](https://aws.amazon.com/free/)
- Render free: "Do not use them for production applications" — [Render Free docs](https://render.com/docs/free)
- Atlas M0: no backups, idle pause, cannot upgrade version — [Atlas Free Cluster Limits](https://www.mongodb.com/docs/atlas/reference/free-shared-limitations/)
- Auth0 Free using paid MFA factors may be blocked — [Auth0 Community](https://community.auth0.com/t/clarification-on-pro-mfa-warning-while-using-totp-in-free-plan/189505)
- UU PDP Art. 56: transfer abroad allowed if destination has equal-or-higher protection; else adequate and binding safeguards; else data-subject consent — [MKRI](https://www.mkri.id/berita/pemohon:-transfer-data-pribadi-ke-luar-negeri-berisiko-lebih-tinggi-23666); [World Law Group](https://www.theworldlawgroup.com/membership/news/personal-data-protection-law-cross-border-transfer-requirements)
- Art. 56 was challenged at the Constitutional Court (Perkara 137/PUU-XXIII/2025); commentary "pasca putusan MK" exists — [hukumonline](https://www.hukumonline.com/berita/a/transfer-data-lintas-negara-dan-ilusi-pelindungan-data-pribadi-pasca-putusan-mk-lt6981468330d9c)
- PP No. 33/2026 reportedly details the cross-border transfer regime, recognizing adequacy decisions and binding corporate rules (not verified against official text) — [Rajah & Tann Indonesia Q1 2026](https://www.rajahtannasia.com/?p=190689) (via search summary)
- Indonesia–US trade arrangement commits to facilitate data transfer to the US (needs ratification) — [Katadata](https://katadata.co.id/amp/digital/teknologi/6880cd6ee6ba8/trump-ingin-perusahaan-as-bisa-transfer-data-wni-ke-luar-negeri-ini-aturannya)
- Indonesia had not published an adequacy list; the PDP agency was not operating as of Sep 2025 — [Mondaq](https://www.mondaq.com/uk/privacy-protection/1684288/data-localisation-and-transfer-issues-in-southeast-asia)

### Inferences
- Auth0 itself stores staff identity data (names/emails) outside Indonesia (Auth0 regions are US/EU/AU/JP — not verified here), so some cross-border transfer exists regardless; document it in a RoPA/DPIA and get staff consent/notice.
- Data-minimal posture: keep business/financial records + evidence files in Jakarta (Azure Indonesia Central, IDCloudHost/Biznet Gio) and only identity in Auth0.
- Oracle Batam is in Indonesia (good for PDP) but the Always Free home-region lock means the tenancy must be created with Batam as home region; A1 capacity there is unknown.

### Gaps
- MK decision outcome for Perkara 137/PUU-XXIII/2025 not confirmed.
- PP 33/2026 text not verified.
- Auth0 tenant region options/data residency not researched.

## Recommendation set (synthesized from above; all costs approximate)

1. **Cheapest workable ("free-for-pilot", ~$0 + Auth0 decision)** — Oracle Always Free A1 (2 OCPU/12 GB, Linux arm64, home region Batam or Singapore) running API + worker + single-node Mongo replica set + Tesseract/ffmpeg via Docker; evidence files on block volume, nightly mongodump + file sync to OCI Object Storage (20 GB) or R2 (10 GB); Cloudflare free DNS/TLS; Vercel frontend. Requires Linux/ARM port. Risks: no SLA, unannounced changes, idle reclamation, capacity shortages. Auth0: Free for pilot only if MFA requirement is relaxed — otherwise +$35/month Essentials.
   - Alternative pilot (no port of DB): Atlas M0 (Singapore) + any always-on compute; 512 MB, no backups.
2. **Recommended paid-minimal (Jakarta, Linux)** — IDCloudHost/Biznet Gio 2–4 vCPU, 4 GB VPS in Indonesia (~Rp105k–165k/month ≈ $6–10) or Azure Indonesia Central B2als v2 Linux (~$31/month + disk) with self-hosted single-node replica set + R2/Object Storage backups; Auth0 Essentials $35/month for MFA. Total ≈ $41–70/month (~Rp0.7–1.15M).
3. **Keep Windows (no port)** — Azure Indonesia Central Windows VM B2s/B2als v2 (~$38–41/month + disk) or IDCloudHost Windows-capable VPS (Windows license cost unverified); self-host Mongo on same VM; Auth0 Essentials $35. Total ≈ $75–90/month. App Service Windows B1 ($54.75) is not suitable alone because it can't host MongoDB or a persistent NTFS evidence disk.
   - Managed DB option: Atlas Flex $8–30/month (5 GB) or M10 ~$58/month (with backups) instead of self-hosted Mongo.
