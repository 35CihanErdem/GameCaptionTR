# GameCaptionTR

Steam / Epic Games üzerinden açılan oyunlarda **ekrandaki altyazıları** okuyup **Türkçeye çeviren** canlı overlay uygulaması.

## Güvenlik yaklaşımı

Bu program:

- oyuna **DLL enjekte etmez**
- oyun belleğini **okumaz / yazmaz**
- global input hook ile **keylogger benzeri** davranış kullanmaz

Sadece:

1. senin seçtiğin ekran bölgesini görüntü olarak alır
2. Windows yerleşik OCR ile metni okur
3. çevrimiçi çeviri servisine gönderir
4. sonucu sürükleyebildiğin şeffaf katmanda gösterir

> Hiçbir yazılım için “asla virüs algılanmaz” garantisi verilemez. Bu mimari false-positive riskini ciddi şekilde azaltır; yine de Windows Defender’da ilk çalıştırmada “tanınmayan uygulama” uyarısı çıkabilir. Kaynak koddan kendin derlediğin için bu normaldir.

## Gereksinimler

- Windows 10/11
- .NET 8 SDK
- Windows OCR dil paketi (ör. English)
  - **Ayarlar → Zaman ve dil → Dil ve bölge → Dil ekle / Dil seçenekleri → Dil paketi / OCR**

## Çalıştırma

```powershell
cd C:\Users\cihan\GameCaptionTR\GameCaptionTR
dotnet run
```

## Yayınlama (tek klasör)

```powershell
cd C:\Users\cihan\GameCaptionTR\GameCaptionTR
dotnet publish -c Release -r win-x64 --self-contained false -o ..\publish
```

`publish\GameCaptionTR.exe` dosyasını çalıştır.

## Kullanım

1. Oyunu aç, altyazıları aç.
2. Uygulamada **Bölge Seç** ile altyazı alanını sürükle.
3. Kaynak dili seç (çoğu oyunda `en`).
4. **Başlat**.
5. Çeviri katmanını istediğin yere sürükle.
6. Oyuna tıklamak için **Tıklamaları oyuna geçir** seçeneğini aç.

## Sınırlar (dürüst not)

- OCR, küçük/bulanık/çok süslü fontlarda hata yapabilir.
- Exclusive Fullscreen bazı oyunlarda ekran yakalamayı zorlaştırır → **Borderless Windowed / Pencere** modu daha iyi çalışır.
- Çevrimiçi çeviri internet ister; aşırı kullanımda geçici limit olabilir.
- Anti-cheat’li rekabetçi oyunlarda overlay’ler bazen engellenebilir; hikâye oyunlarında genelde sorun olmaz.

## Klasör

```
GameCaptionTR/
  GameCaptionTR/          # WPF uygulama
  README.md
```
