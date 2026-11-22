# Medfølgende ESP-firmware: ingen

Fleetdeck har ingen firmware med. Firmwaren hentes fra et katalog, brugeren selv sætter op på siden ESP-flash → Indstillinger:
adressen på mappen med `catalog.json` (samme format som ESP Flasher), fx `https://github.com/EJER/REPO/tree/main/mappe`, og
en nøgle, hvis repoet er privat.

`catalog.json` her er et tomt katalog, så serveren har noget at starte fra. Testen `EspFlashTests.BundledCatalogMatchesFiles`
kontrollerer, at kataloget er gyldigt, og at hver firmware i det (hvis nogen) passer med sin sha256.
