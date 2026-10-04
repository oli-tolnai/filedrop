# FileDrop

Egyszerű, magyar nyelvű fájlátadás az otthoni hálózaton. Az Angular felület és az ASP.NET Core API egyetlen, korlátozott jogosultságú Docker-konténerben fut; a fájlok és a SQLite-adatbázis külön menthető könyvtárban maradnak.

## Elkészült működés

- Telefonon és laptopon használható reszponzív böngészős felület.
- Közös LAN-listában látható, illetve linkkel, QR-kóddal vagy négyjegyű kóddal elérhető rejtett megosztás.
- Lejárat: első sikeres letöltés, 15 perc, 1 óra (alapértelmezett), 24 óra, 7 nap vagy kézi visszavonás.
- Valódi fájlfeltöltés folyamatjelzővel és letöltés eredeti fájlnéven.
- Tulajdonosi és családtag-fiókok biztonságosan hash-elt jelszóval.
- A fiókjelszó legalább 4 számjegy lehet; ez csak megbízható LAN-on kényelmes, Tailscale-es távoli használat előtt erősebb védelem szükséges.
- Egy fiókhoz több, külön megnevezett eszköz munkamenete tartozhat; minden eszköz ugyanazt a saját előzményt látja.
- A tulajdonos új családtag-fiókot hozhat létre. A saját aktív megosztás visszavonható; a fizikai fájl törlődik, az előzmény megmarad.
- Bejelentkezés nélkül is lehet LAN-on feltölteni, de az ilyen feltöltés utólag nem jelenik meg senki saját előzményeiben.
- A kódpróbálkozás és a bejelentkezés sebességkorlátozott.
- A lejárt fájlok és munkamenetek automatikusan takarítódnak, újraindítás után is.

## Tárhelyszabályok

- Nincs külön fájlméretkorlát: egy fájl addig tölthető fel, amíg a biztonsági tartalék megmarad.
- Nincs előre lefoglalt vagy fix összesített keret.
- A program a tényleges fájlrendszer-szabadhelyet figyeli, és mindig 100 GiB tartalékot hagy.
- A felület külön mutatja a FileDrop-fájlok méretét, a még feltölthető mennyiséget és a tartalékot.
- A párhuzamos feltöltések helyét előre lefoglalja, ezért együtt sem léphetik át a biztonságos kapacitást.
- A Windows-partíciókhoz nem nyúl. A tervezett adatútvonal `/srv/filedrop/data`, ugyanazon az Ubuntu ext4 fájlrendszeren, külön partíció és előfoglalás nélkül.

## Helyi fejlesztés

Követelmény: .NET 10 SDK, Node.js 24, pnpm 11.10.0.

API:

```powershell
cd src/FileDrop.Api
dotnet run --urls http://127.0.0.1:5199
```

Felület egy másik terminálban:

```powershell
cd src/FileDrop.Web
pnpm install --frozen-lockfile
pnpm start -- --host 127.0.0.1
```

A helyi fejlesztéshez az API indítása előtt adj meg külön egyszeri beállítási kódot a környezetben. A Docker/éles környezethez mindig külön, hosszú véletlen kódot használj.

```powershell
$env:FileDrop__SetupToken='csak-a-helyi-fejleszteshez'
dotnet run --urls http://127.0.0.1:5199
```

## Docker

Ellenőrzés és build:

```powershell
$env:FILEDROP_SETUP_TOKEN='csak-helyi-ellenorzeshez'
docker compose config --quiet
docker compose build
```

A Compose alapértelmezetten csak a Dell LAN-címén publikál: `192.168.0.34:8090`. Nem nyit routerportot, nem hoz létre Cloudflare Tunnel útvonalat, és egyelőre nem publikál külön a Tailscale-interfészen.

Az éles `.env` fájl nem kerül verziókezelésbe. A konténer nem rootként fut, a saját rendszerfájlrendszere csak olvasható, minden capability el van dobva, és kizárólag `/srv/filedrop/data` írható számára.

## Biztonsági határ

A mostani LAN-változat HTTP-t használ. Ez családi, megbízható hálózaton kényelmes, de a bejelentkezési forgalom nincs titkosítva, ezért nyilvános Wi-Fi-re vagy internetre nem szabad így kitenni. Tailscale-es távoli használat előtt HTTPS-t és szűk Tailscale-hozzáférést állítunk be; publikus idegennek csak külön, lejáró letöltési/feltöltési végpont készülhet.

## Ellenőrzött állapot

- `dotnet build`: sikeres, figyelmeztetés nélkül.
- `pnpm build`: sikeres, figyelmeztetés nélkül.
- Valódi böngészős feltöltés, kód és QR: sikeres.
- Letöltött fájl SHA-256 egyezés: sikeres.
- Többeszközös belépés, saját előzmény és visszavonás: sikeres.
- Docker image build és korlátozott jogosultságú futtatás: sikeres.

## Következő lépések

1. Jóváhagyás után telepítés a Dellre `/opt/stacks/filedrop` és `/srv/filedrop/data` útvonalakkal, csak a `192.168.0.34:8090` LAN-címen.
2. Helyi telefonos/laptopos próba és mentési eljárás.
3. HTTPS és külön Tailscale-hozzáférés.
4. Android alkalmazás a Megosztás menü integrációjával.
5. Külön, lejáró publikus letöltési link és feltöltési kérés idegeneknek.
