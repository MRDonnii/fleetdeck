/*
 * WinSetup – grafisk installer til netværksbootet (WinPE).
 *
 * Kører i Windows Setups WinPE, som hverken har PowerShell, .NET eller HTA. Programmet bruger kun Win32, GDI/GDI+
 * og netværks-API'erne, og bygges med mingw-w64 (se build.sh). Flow:
 *   vent på netværk -> log ind på \\SERVER\SHARE -> find MAC -> vis udrulning fra Fleetdeck eller valg
 *   (helt ren / med mine ting / profil) -> pc-navn og bruger -> svarfil -> Windows Setup.
 *
 * Argumenter:  /server 192.168.1.10  /share PC-Reinstall  /root Z:\WinSetup  /name "Min server"
 *              (pxe-build.ps1 sætter /server, /share og /name i winpeshl.ini)
 *              /test <mappe>      (ingen login, ingen setup.exe; mappen bruges som WinSetup-roden)
 *              /shots <mappe>     (sammen med /test: gem et billede af hver skærm som .bmp og afslut)
 */
#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <shellapi.h>
#include <tlhelp32.h>
#include <winioctl.h>
#include <iphlpapi.h>
#include <winnetwk.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include <wctype.h>

/* ------------------------------------------------------------------ GDI+ (flad API, kun det vi bruger) */
typedef struct { UINT32 GdiplusVersion; void *DebugEventCallback; BOOL SuppressBackgroundThread; BOOL SuppressExternalCodecs; } GpStartupInput;
typedef void GpGraphics, GpBrush, GpPath, GpPen;
typedef struct { float X, Y, Width, Height; } GpRectF;
int WINAPI GdiplusStartup(ULONG_PTR *, const GpStartupInput *, void *);
int WINAPI GdipCreateFromHDC(HDC, GpGraphics **);
int WINAPI GdipDeleteGraphics(GpGraphics *);
int WINAPI GdipSetSmoothingMode(GpGraphics *, int);
int WINAPI GdipCreateSolidFill(DWORD, GpBrush **);
int WINAPI GdipCreateLineBrushFromRect(const GpRectF *, DWORD, DWORD, int, int, GpBrush **);
int WINAPI GdipDeleteBrush(GpBrush *);
int WINAPI GdipCreatePath(int, GpPath **);
int WINAPI GdipDeletePath(GpPath *);
int WINAPI GdipAddPathArc(GpPath *, float, float, float, float, float, float);
int WINAPI GdipClosePathFigure(GpPath *);
int WINAPI GdipFillPath(GpGraphics *, GpBrush *, GpPath *);
int WINAPI GdipDrawPath(GpGraphics *, GpPen *, GpPath *);
int WINAPI GdipCreatePen1(DWORD, float, int, GpPen **);
int WINAPI GdipDeletePen(GpPen *);
int WINAPI GdipSetPenStartCap(GpPen *, int);
int WINAPI GdipSetPenEndCap(GpPen *, int);
int WINAPI GdipDrawArc(GpGraphics *, GpPen *, float, float, float, float, float, float);
int WINAPI GdipDrawLine(GpGraphics *, GpPen *, float, float, float, float);
int WINAPI GdipFillRectangle(GpGraphics *, GpBrush *, float, float, float, float);
int WINAPI GdipFillEllipse(GpGraphics *, GpBrush *, float, float, float, float);
int WINAPI GdipDrawEllipse(GpGraphics *, GpPen *, float, float, float, float);
int WINAPI GdipFlush(GpGraphics *, int);
int WINAPI GdipAddPathEllipse(GpPath *, float, float, float, float);
int WINAPI GdipCreatePathGradientFromPath(const GpPath *, GpBrush **);
int WINAPI GdipSetPathGradientCenterColor(GpBrush *, DWORD);
int WINAPI GdipSetPathGradientSurroundColorsWithCount(GpBrush *, const DWORD *, int *);

/* ------------------------------------------------------------------ farver (ARGB) */
#define C_BG1     0xFF0A1020
#define C_BG2     0xFF16224A
#define C_CARD    0xFF172138
#define C_CARDHI  0xFF1F2C4C
#define C_FIELD   0xFF0E1628
#define C_BORDER  0xFF2B3A5C
#define C_ACCENT  0xFF3B82F6
#define C_ACCHI   0xFF60A5FA
#define C_GREEN   0xFF34D399
#define C_RED     0xFFF87171
#define C_AMBER   0xFFFBBF24
#define C_ORANGE  0xFFE95420
#define RGBOF(c)  RGB(((c) >> 16) & 0xFF, ((c) >> 8) & 0xFF, (c) & 0xFF)
#define T_WHITE   RGB(241, 245, 249)
#define T_MUTED   RGB(148, 163, 184)
#define T_DIM     RGB(100, 116, 139)

/* ------------------------------------------------------------------ tilstand */
enum { SC_NET, SC_LOGIN, SC_DEPLOY, SC_CHOOSE, SC_PROFILE, SC_DETAILS, SC_UBUNTU, SC_RUNNING, SC_ERROR, SC_COUNT };
enum { M_CLEAN = 1, M_MANAGED = 2, M_PROFILE = 3 };
enum {
    B_NONE, B_LOGIN, B_RETRY, B_CMD, B_REBOOT, B_BACK, B_START, B_CHOOSE_SELF,
    B_MODE1, B_MODE2, B_MODE3, B_UBUNTU, B_INSTALL, B_AUTODISK, B_PROF0 = 100
};
#define WM_LOGIN_DONE (WM_APP + 1)
#define WM_SETUP_DONE (WM_APP + 2)
#define WM_PRIMARY    (WM_APP + 3)
#define WM_BACKKEY    (WM_APP + 4)

typedef struct { RECT r; int id; } Hit;
typedef struct { wchar_t id[16]; wchar_t name[128]; } Profile;

static HWND g_wnd, g_eUser, g_ePass, g_ePc, g_eWin;
static HFONT f_title, f_h2, f_body, f_small, f_btn, f_edit, f_mono, f_big, f_icon, f_iconS, f_key;
static HBRUSH g_fieldBrush;
static int g_screen = SC_NET, g_hover = B_NONE, g_busy, g_tick, g_W, g_H;
static float g_s = 1.0f;
static Hit g_hits[64]; static int g_nhits;
static RECT g_rUser, g_rPass, g_rPc, g_rWin;
static wchar_t g_err[512], g_info[512];
static wchar_t g_server[64] = L"", g_share[64] = L"PC-Reinstall", g_root[MAX_PATH] = L"Z:\\WinSetup", g_name[64] = L"", g_label[96] = L"";
static wchar_t g_outXml[MAX_PATH] = L"X:\\unattend.xml";
static BOOL g_test; static wchar_t g_shots[MAX_PATH];
static wchar_t g_mac[32], g_ip[64];
static wchar_t g_defUser[64] = L"", g_defProfName[128];
static Profile g_prof[48]; static int g_nprof, g_profSel = -1;
static BOOL g_hasDeploy;
static wchar_t D_ID[16], D_MODE[16], D_PROFILE[16], D_PROFNAME[128], D_PC[64], D_USER[64];
static int g_mode = M_MANAGED;
static int g_netWait;
/* diske og VM: automatisk diskvalg er slået til som standard i en VM */
static BOOL g_isVm, g_autoDisk;
static int g_ndisks;
static wchar_t g_disk0[160], g_machine[160];
static void CopyMax(wchar_t *dst, size_t cap, const wchar_t *src);

static void ReadMachine(void)
{
    HKEY k; wchar_t man[80] = L"", prod[80] = L""; DWORD n;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"HARDWARE\\DESCRIPTION\\System\\BIOS", 0, KEY_READ, &k) == ERROR_SUCCESS)
    {
        n = sizeof man; RegQueryValueExW(k, L"SystemManufacturer", NULL, NULL, (BYTE *)man, &n);
        n = sizeof prod; RegQueryValueExW(k, L"SystemProductName", NULL, NULL, (BYTE *)prod, &n);
        RegCloseKey(k);
    }
    swprintf(g_machine, 160, L"%s %s", man, prod);
    wchar_t up[160]; CopyMax(up, 160, g_machine); CharUpperW(up);
    g_isVm = wcsstr(up, L"QEMU") || wcsstr(up, L"VMWARE") || wcsstr(up, L"VIRTUALBOX") || wcsstr(up, L"VIRTUAL MACHINE") || wcsstr(up, L"KVM") || wcsstr(up, L"BOCHS");
}

/* Fysiske diske: antal og disk 0 (model og størrelse) */
static void ReadDisks(void)
{
    g_ndisks = 0; g_disk0[0] = 0;
    for (int i = 0; i < 16; i++)
    {
        wchar_t p[40]; swprintf(p, 40, L"\\\\.\\PhysicalDrive%d", i);
        HANDLE h = CreateFileW(p, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
        if (h == INVALID_HANDLE_VALUE) h = CreateFileW(p, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
        if (h == INVALID_HANDLE_VALUE) continue;
        GET_LENGTH_INFORMATION len = { 0 }; DWORD got;
        BOOL okLen = DeviceIoControl(h, IOCTL_DISK_GET_LENGTH_INFO, NULL, 0, &len, sizeof len, &got, NULL);
        STORAGE_PROPERTY_QUERY q = { StorageDeviceProperty, PropertyStandardQuery };
        BYTE buf[1024] = { 0 }; wchar_t model[100] = L"Disk";
        if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, &q, sizeof q, buf, sizeof buf, &got, NULL))
        {
            STORAGE_DEVICE_DESCRIPTOR *d = (STORAGE_DEVICE_DESCRIPTOR *)buf;
            if (d->BusType == BusTypeUsb) { CloseHandle(h); continue; }   /* USB-nøgler tæller ikke */
            if (d->ProductIdOffset && d->ProductIdOffset < sizeof buf)
            {
                MultiByteToWideChar(CP_ACP, 0, (char *)buf + d->ProductIdOffset, -1, model, 100);
                for (int j = (int)wcslen(model) - 1; j >= 0 && model[j] == L' '; j--) model[j] = 0;
            }
        }
        CloseHandle(h);
        if (i == 0)
        {
            if (okLen) swprintf(g_disk0, 160, L"%s · %.0f GB", model, len.Length.QuadPart / 1e9);
            else CopyMax(g_disk0, 160, model);
        }
        g_ndisks++;
    }
    g_autoDisk = g_isVm && g_ndisks >= 1 && g_disk0[0];
}

/* Disk 0 slettes og deles op: EFI, MSR og Windows (UEFI/GPT) */
static const wchar_t *DISK_XML =
    L"<DiskConfiguration>"
    L"<Disk wcm:action=\"add\"><DiskID>0</DiskID><WillWipeDisk>true</WillWipeDisk><CreatePartitions>"
    L"<CreatePartition wcm:action=\"add\"><Order>1</Order><Type>EFI</Type><Size>260</Size></CreatePartition>"
    L"<CreatePartition wcm:action=\"add\"><Order>2</Order><Type>MSR</Type><Size>16</Size></CreatePartition>"
    L"<CreatePartition wcm:action=\"add\"><Order>3</Order><Type>Primary</Type><Extend>true</Extend></CreatePartition>"
    L"</CreatePartitions><ModifyPartitions>"
    L"<ModifyPartition wcm:action=\"add\"><Order>1</Order><PartitionID>1</PartitionID><Format>FAT32</Format><Label>System</Label></ModifyPartition>"
    L"<ModifyPartition wcm:action=\"add\"><Order>2</Order><PartitionID>2</PartitionID></ModifyPartition>"
    L"<ModifyPartition wcm:action=\"add\"><Order>3</Order><PartitionID>3</PartitionID><Format>NTFS</Format><Label>Windows</Label><Letter>C</Letter></ModifyPartition>"
    L"</ModifyPartitions></Disk></DiskConfiguration>"
    L"<ImageInstall><OSImage><InstallTo><DiskID>0</DiskID><PartitionID>3</PartitionID></InstallTo>"
    L"<InstallToAvailablePartition>false</InstallToAvailablePartition></OSImage></ImageInstall>";
static wchar_t g_loginUser[128], g_loginPass[128];

/* ------------------------------------------------------------------ små hjælpere */
static int S(float v) { return (int)(v * g_s + 0.5f); }

static void SetHit(RECT r, int id) { if (g_nhits < 64) { g_hits[g_nhits].r = r; g_hits[g_nhits].id = id; g_nhits++; } }

static HFONT MkFont(const wchar_t *face, float px, int weight)
{
    return CreateFontW(-S(px), 0, 0, 0, weight, 0, 0, 0, DEFAULT_CHARSET, OUT_TT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                       DEFAULT_PITCH, face);
}

static void MakeFonts(void)
{
    HFONT *all[] = { &f_title, &f_h2, &f_body, &f_small, &f_btn, &f_edit, &f_mono, &f_big, &f_icon, &f_iconS, &f_key };
    for (int i = 0; i < 11; i++) if (*all[i]) DeleteObject(*all[i]);
    f_icon  = MkFont(L"Segoe UI Symbol", 26, FW_NORMAL);
    f_iconS = MkFont(L"Segoe UI Symbol", 18, FW_NORMAL);
    f_key   = MkFont(L"Segoe UI Semibold", 13, FW_SEMIBOLD);
    f_title = MkFont(L"Segoe UI Semibold", 34, FW_SEMIBOLD);
    f_h2    = MkFont(L"Segoe UI Semibold", 22, FW_SEMIBOLD);
    f_body  = MkFont(L"Segoe UI", 17, FW_NORMAL);
    f_small = MkFont(L"Segoe UI", 14.5f, FW_NORMAL);
    f_btn   = MkFont(L"Segoe UI Semibold", 17, FW_SEMIBOLD);
    f_edit  = MkFont(L"Segoe UI", 19, FW_NORMAL);
    f_mono  = MkFont(L"Consolas", 30, FW_NORMAL);
    f_big   = MkFont(L"Segoe UI Semilight", 46, FW_LIGHT);
}

static BOOL FileExists(const wchar_t *p) { DWORD a = GetFileAttributesW(p); return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY); }

/* Læs en tekstfil (UTF-8) som wchar_t. Kalderen frigiver med free(). */
static wchar_t *ReadText(const wchar_t *path)
{
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return NULL;
    DWORD n = GetFileSize(h, NULL), got = 0;
    char *buf = (char *)malloc(n + 1);
    if (!buf) { CloseHandle(h); return NULL; }
    ReadFile(h, buf, n, &got, NULL); CloseHandle(h);
    buf[got] = 0;
    char *b = buf; if (got >= 3 && (unsigned char)b[0] == 0xEF && (unsigned char)b[1] == 0xBB && (unsigned char)b[2] == 0xBF) b += 3;
    int wn = MultiByteToWideChar(CP_UTF8, 0, b, -1, NULL, 0);
    wchar_t *w = (wchar_t *)malloc(sizeof(wchar_t) * (wn + 1));
    MultiByteToWideChar(CP_UTF8, 0, b, -1, w, wn);
    free(buf);
    return w;
}

static BOOL WriteUtf8(const wchar_t *path, const wchar_t *text)
{
    int n = WideCharToMultiByte(CP_UTF8, 0, text, -1, NULL, 0, NULL, NULL);
    char *b = (char *)malloc(n);
    WideCharToMultiByte(CP_UTF8, 0, text, -1, b, n, NULL, NULL);
    HANDLE h = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) { free(b); return FALSE; }
    DWORD w; BOOL ok = WriteFile(h, b, n - 1, &w, NULL); CloseHandle(h); free(b);
    return ok;
}

/* Linjer af typen  set "NAVN=værdi"  -> kald cb(navn, værdi) */
static void ParseSetFile(const wchar_t *path, void (*cb)(const wchar_t *, const wchar_t *))
{
    wchar_t *t = ReadText(path);
    if (!t) return;
    for (wchar_t *line = wcstok(t, L"\r\n", NULL); line; line = wcstok(NULL, L"\r\n", NULL))
    {
        wchar_t *p = wcsstr(line, L"set \"");
        if (!p) continue;
        p += 5;
        wchar_t *eq = wcschr(p, L'='), *end = wcsrchr(p, L'"');
        if (!eq || !end || end < eq) continue;
        *eq = 0; *end = 0;
        cb(p, eq + 1);
    }
    free(t);
}

static void CopyMax(wchar_t *dst, size_t cap, const wchar_t *src) { wcsncpy(dst, src, cap - 1); dst[cap - 1] = 0; }

static void OnDefault(const wchar_t *k, const wchar_t *v)
{
    if (!wcscmp(k, L"DEF_USER") && *v) CopyMax(g_defUser, 64, v);
    else if (!wcscmp(k, L"DEF_PROFILENAME")) CopyMax(g_defProfName, 128, v);
}

static void OnDeploy(const wchar_t *k, const wchar_t *v)
{
    if (!wcscmp(k, L"D_ID")) CopyMax(D_ID, 16, v);
    else if (!wcscmp(k, L"D_MODE")) CopyMax(D_MODE, 16, v);
    else if (!wcscmp(k, L"D_PROFILE")) CopyMax(D_PROFILE, 16, v);
    else if (!wcscmp(k, L"D_PROFILENAME")) CopyMax(D_PROFNAME, 128, v);
    else if (!wcscmp(k, L"PCNAME")) CopyMax(D_PC, 64, v);
    else if (!wcscmp(k, L"WUSER")) CopyMax(D_USER, 64, v);
}

/* ------------------------------------------------------------------ netværk */
static BOOL FindNetwork(void)
{
    ULONG len = 32 * 1024;
    IP_ADAPTER_ADDRESSES *a = (IP_ADAPTER_ADDRESSES *)malloc(len);
    BOOL found = FALSE;
    if (GetAdaptersAddresses(AF_INET, GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER, NULL, a, &len) == NO_ERROR)
    {
        for (IP_ADAPTER_ADDRESSES *p = a; p; p = p->Next)
        {
            if (p->IfType == IF_TYPE_SOFTWARE_LOOPBACK || p->PhysicalAddressLength != 6 || p->OperStatus != IfOperStatusUp) continue;
            if (!g_mac[0] || !found)
                swprintf(g_mac, 32, L"%02X-%02X-%02X-%02X-%02X-%02X", p->PhysicalAddress[0], p->PhysicalAddress[1], p->PhysicalAddress[2],
                         p->PhysicalAddress[3], p->PhysicalAddress[4], p->PhysicalAddress[5]);
            for (IP_ADAPTER_UNICAST_ADDRESS *u = p->FirstUnicastAddress; u; u = u->Next)
            {
                struct sockaddr_in *sin = (struct sockaddr_in *)u->Address.lpSockaddr;
                unsigned char *b = (unsigned char *)&sin->sin_addr;
                if (b[0] == 169 && b[1] == 254) continue;
                swprintf(g_ip, 64, L"%u.%u.%u.%u", b[0], b[1], b[2], b[3]);
                found = TRUE;
                break;
            }
            if (found) break;
        }
    }
    free(a);
    return found;
}

static DWORD WINAPI LoginThread(LPVOID unused)
{
    (void)unused;
    wchar_t unc[160]; swprintf(unc, 160, L"\\\\%s\\%s", g_server, g_share);
    NETRESOURCEW nr = { 0 };
    nr.dwType = RESOURCETYPE_DISK; nr.lpLocalName = L"Z:"; nr.lpRemoteName = unc;
    DWORD r = ERROR_BAD_NETPATH;
    /* Lige efter opstart er WinPE's netværksklient ikke altid klar ("kan ikke finde"): prøv igen et par gange */
    for (int attempt = 0; attempt < 8; attempt++)
    {
        r = WNetAddConnection2W(&nr, g_loginPass, g_loginUser, 0);
        if (r == ERROR_ALREADY_ASSIGNED || r == ERROR_SESSION_CREDENTIAL_CONFLICT || r == ERROR_DEVICE_ALREADY_REMEMBERED)
        {
            WNetCancelConnection2W(L"Z:", 0, TRUE);
            r = WNetAddConnection2W(&nr, g_loginPass, g_loginUser, 0);
        }
        if (!(r == ERROR_BAD_NETPATH || r == ERROR_BAD_NET_NAME || r == ERROR_NO_NET_OR_BAD_PATH || r == ERROR_NETWORK_UNREACHABLE ||
              r == ERROR_NO_NETWORK || r == ERROR_UNEXP_NET_ERR || r == ERROR_SEM_TIMEOUT)) break;
        Sleep(2500);
    }
    SecureZeroMemory(g_loginPass, sizeof g_loginPass);
    PostMessageW(g_wnd, WM_LOGIN_DONE, r, 0);
    return 0;
}

static HANDLE g_setupProc;
static int g_setupGone;

/* Kører Windows Setup stadig (setup.exe, SetupHost.exe, SetupPrep.exe)? */
static BOOL SetupRunning(void)
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return FALSE;
    PROCESSENTRY32W pe = { sizeof pe }; BOOL found = FALSE;
    for (BOOL ok = Process32FirstW(snap, &pe); ok && !found; ok = Process32NextW(snap, &pe))
        found = !_wcsicmp(pe.szExeFile, L"setup.exe") || !_wcsicmp(pe.szExeFile, L"SetupHost.exe") || !_wcsicmp(pe.szExeFile, L"SetupPrep.exe");
    CloseHandle(snap);
    return found;
}
static DWORD WINAPI WaitSetupThread(LPVOID unused)
{
    (void)unused;
    WaitForSingleObject(g_setupProc, INFINITE);
    DWORD code = 0; GetExitCodeProcess(g_setupProc, &code);
    PostMessageW(g_wnd, WM_SETUP_DONE, code, 0);
    return 0;
}

static void Run(const wchar_t *cmdline, BOOL newConsole)
{
    STARTUPINFOW si = { sizeof si }; PROCESS_INFORMATION pi;
    wchar_t c[512]; CopyMax(c, 512, cmdline);
    if (CreateProcessW(NULL, c, NULL, NULL, FALSE, newConsole ? CREATE_NEW_CONSOLE : 0, NULL, NULL, &si, &pi))
    { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
}

/* ------------------------------------------------------------------ data fra sharen */
static void LoadData(void)
{
    wchar_t p[MAX_PATH];
    swprintf(p, MAX_PATH, L"%s\\PXE\\deploy\\defaults.cmd", g_root); ParseSetFile(p, OnDefault);
    g_nprof = 0;
    swprintf(p, MAX_PATH, L"%s\\PXE\\deploy\\profiles.txt", g_root);
    wchar_t *t = ReadText(p);
    if (t)
    {
        for (wchar_t *line = wcstok(t, L"\r\n", NULL); line && g_nprof < 48; line = wcstok(NULL, L"\r\n", NULL))
        {
            wchar_t *bar = wcschr(line, L'|');
            if (!bar) continue;
            *bar = 0;
            CopyMax(g_prof[g_nprof].id, 16, line); CopyMax(g_prof[g_nprof].name, 128, bar + 1);
            g_nprof++;
        }
        free(t);
    }
    g_hasDeploy = FALSE;
    if (g_mac[0])
    {
        swprintf(p, MAX_PATH, L"%s\\PXE\\deploy\\%s.cmd", g_root, g_mac);
        if (FileExists(p))
        {
            D_ID[0] = D_PROFILE[0] = D_PROFNAME[0] = D_PC[0] = D_USER[0] = 0; wcscpy(D_MODE, L"managed");
            ParseSetFile(p, OnDeploy);
            g_hasDeploy = D_PC[0] && D_USER[0];
        }
    }
}

static BOOL CheckFiles(void)
{
    wchar_t p[MAX_PATH];
    swprintf(p, MAX_PATH, L"%s\\PXE\\win11\\setup.exe", g_root);
    if (!FileExists(p)) { swprintf(g_err, 512, L"Fandt ikke %s.\nLigger Windows-installationen på sharen?", p); return FALSE; }
    swprintf(p, MAX_PATH, L"%s\\USB\\autounattend-pxe.xml", g_root);
    if (!FileExists(p)) { swprintf(g_err, 512, L"Fandt ikke svarfil-skabelonen %s.", p); return FALSE; }
    return TRUE;
}

/* ------------------------------------------------------------------ validering */
static BOOL ValidPc(const wchar_t *s)
{
    size_t n = wcslen(s);
    if (n < 1 || n > 15 || s[0] == L'-') return FALSE;
    for (size_t i = 0; i < n; i++) { wchar_t c = s[i]; if (!((c >= L'A' && c <= L'Z') || (c >= L'a' && c <= L'z') || (c >= L'0' && c <= L'9') || c == L'-')) return FALSE; }
    return TRUE;
}

static BOOL ValidUser(const wchar_t *s)
{
    size_t n = wcslen(s);
    if (n < 1 || n > 20 || !iswalnum(s[0]) || s[n - 1] == L'.' || s[n - 1] == L' ') return FALSE;
    for (size_t i = 0; i < n; i++) { wchar_t c = s[i]; if (!((c >= L'A' && c <= L'Z') || (c >= L'a' && c <= L'z') || (c >= L'0' && c <= L'9') || c == L' ' || c == L'.' || c == L'_' || c == L'-')) return FALSE; }
    return TRUE;
}

/* ------------------------------------------------------------------ svarfil og installation */
static wchar_t *Replace(wchar_t *src, const wchar_t *token, const wchar_t *value)
{
    size_t tl = wcslen(token), vl = wcslen(value), count = 0;
    for (wchar_t *p = src; (p = wcsstr(p, token)); p += tl) count++;
    if (!count) return src;
    wchar_t *out = (wchar_t *)malloc(sizeof(wchar_t) * (wcslen(src) + count * (vl + 1) + 1)), *o = out, *p = src, *q;
    while ((q = wcsstr(p, token))) { memcpy(o, p, (q - p) * sizeof(wchar_t)); o += q - p; memcpy(o, value, vl * sizeof(wchar_t)); o += vl; p = q + tl; }
    wcscpy(o, p);
    free(src);
    return out;
}

static void StartInstall(const wchar_t *pc, const wchar_t *user, const wchar_t *mode, const wchar_t *profile, const wchar_t *deployId)
{
    wchar_t p[MAX_PATH], up[16];
    swprintf(p, MAX_PATH, L"%s\\USB\\autounattend-pxe.xml", g_root);
    wchar_t *x = ReadText(p);
    if (!x) { swprintf(g_err, 512, L"Kunne ikke læse %s", p); g_screen = SC_ERROR; return; }
    CopyMax(up, 16, pc); CharUpperW(up);
    x = Replace(x, L"__PCNAME__", up);
    x = Replace(x, L"__USER__", user);
    x = Replace(x, L"__MODE__", mode);
    x = Replace(x, L"__PROFILE__", profile);
    x = Replace(x, L"__DEPLOY__", deployId);
    x = Replace(x, L"__DISKCONFIG__", g_autoDisk && g_ndisks ? DISK_XML : L"");
    BOOL ok = !wcsstr(x, L"__PCNAME__") && WriteUtf8(g_outXml, x);
    free(x);
    if (!ok) { swprintf(g_err, 512, L"Kunne ikke skrive svarfilen %s", g_outXml); g_screen = SC_ERROR; return; }
    if (deployId[0])
    {
        /* Fortæl Fleetdeck at udrulningen er taget i brug */
        wchar_t used[MAX_PATH], body[32];
        swprintf(used, MAX_PATH, L"%s\\PXE\\deploy\\%s.used", g_root, g_mac);
        swprintf(body, 32, L"%s\r\n", deployId);
        WriteUtf8(used, body);
    }
    swprintf(g_info, 512, L"%s  ·  bruger %s", up, user);
    g_screen = SC_RUNNING;
    if (g_test) return;
    wchar_t cmd[600];
    swprintf(cmd, 600, L"\"%s\\PXE\\win11\\setup.exe\" /unattend:\"%s\"", g_root, g_outXml);
    STARTUPINFOW si = { sizeof si }; PROCESS_INFORMATION pi;
    if (!CreateProcessW(NULL, cmd, NULL, NULL, FALSE, 0, NULL, NULL, &si, &pi))
    { swprintf(g_err, 512, L"Kunne ikke starte Windows Setup (fejl %lu)", GetLastError()); g_screen = SC_ERROR; return; }
    CloseHandle(pi.hThread);
    g_setupProc = pi.hProcess;
    CreateThread(NULL, 0, WaitSetupThread, NULL, 0, NULL);
    SetTimer(g_wnd, 2, 4000, NULL);   /* skjul os, når Setup er kommet frem */
}

/* ------------------------------------------------------------------ tegning */
typedef struct { HDC dc; GpGraphics *g; } Ctx;

static void RoundPath(GpPath *p, float x, float y, float w, float h, float r)
{
    float d = r * 2;
    GdipAddPathArc(p, x, y, d, d, 180, 90);
    GdipAddPathArc(p, x + w - d, y, d, d, 270, 90);
    GdipAddPathArc(p, x + w - d, y + h - d, d, d, 0, 90);
    GdipAddPathArc(p, x, y + h - d, d, d, 90, 90);
    GdipClosePathFigure(p);
}

static void FillRound(Ctx *c, RECT r, float rad, DWORD fill, DWORD border, float bw)
{
    GpPath *p; GdipCreatePath(0, &p);
    RoundPath(p, (float)r.left, (float)r.top, (float)(r.right - r.left), (float)(r.bottom - r.top), rad);
    if (fill) { GpBrush *b; GdipCreateSolidFill(fill, &b); GdipFillPath(c->g, b, p); GdipDeleteBrush(b); }
    if (border) { GpPen *pen; GdipCreatePen1(border, bw, 2, &pen); GdipDrawPath(c->g, pen, p); GdipDeletePen(pen); }
    GdipDeletePath(p);
}

static void Text(Ctx *c, HFONT f, COLORREF col, RECT r, const wchar_t *s, UINT fmt)
{
    GdipFlush(c->g, 1);
    SelectObject(c->dc, f); SetTextColor(c->dc, col); SetBkMode(c->dc, TRANSPARENT);
    DrawTextW(c->dc, s, -1, &r, fmt | DT_NOPREFIX);
}

static int TextHeight(Ctx *c, HFONT f, int width, const wchar_t *s)
{
    RECT r = { 0, 0, width, 0 };
    SelectObject(c->dc, f);
    DrawTextW(c->dc, s, -1, &r, DT_CALCRECT | DT_WORDBREAK | DT_NOPREFIX);
    return r.bottom;
}

static RECT Rc(int x, int y, int w, int h) { RECT r = { x, y, x + w, y + h }; return r; }

static void FillRoundGrad(Ctx *c, RECT r, float rad, DWORD c1, DWORD c2, int mode)
{
    GpPath *p; GdipCreatePath(0, &p);
    RoundPath(p, (float)r.left, (float)r.top, (float)(r.right - r.left), (float)(r.bottom - r.top), rad);
    GpRectF rf = { (float)r.left - 1, (float)r.top - 1, (float)(r.right - r.left) + 2, (float)(r.bottom - r.top) + 2 };
    GpBrush *b; GdipCreateLineBrushFromRect(&rf, c1, c2, mode, 0, &b);
    GdipFillPath(c->g, b, p); GdipDeleteBrush(b); GdipDeletePath(p);
}

/* Blødt lys: elliptisk gradient fra farve til gennemsigtig */
static void Glow(Ctx *c, float cx, float cy, float rx, float ry, DWORD center)
{
    GpPath *p; GdipCreatePath(0, &p);
    GdipAddPathEllipse(p, cx - rx, cy - ry, rx * 2, ry * 2);
    GpBrush *b; GdipCreatePathGradientFromPath(p, &b);
    GdipSetPathGradientCenterColor(b, center);
    DWORD sur = center & 0x00FFFFFF; int n = 1;
    GdipSetPathGradientSurroundColorsWithCount(b, &sur, &n);
    GdipFillPath(c->g, b, p);
    GdipDeleteBrush(b); GdipDeletePath(p);
}

static void Button(Ctx *c, RECT r, int id, const wchar_t *label, BOOL primary)
{
    BOOL hov = g_hover == id && !g_busy;
    if (primary)
    {
        FillRound(c, Rc(r.left, r.top + S(4), r.right - r.left, r.bottom - r.top), S(12), hov ? 0x553B82F6 : 0x332563EB, 0, 0);   /* glød */
        FillRoundGrad(c, r, S(12), hov ? 0xFF60A5FA : 0xFF3B82F6, hov ? 0xFF818CF8 : 0xFF6366F1, 0);
    }
    else FillRound(c, r, S(12), hov ? C_CARDHI : 0x18FFFFFF, hov ? C_ACCHI : C_BORDER, S(1.3f));
    Text(c, f_btn, RGB(255, 255, 255), r, label, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
    SetHit(r, id);
}

/* Farvet ikonflise med symbol (Segoe UI Symbol) eller tegnet ikon */
enum { IC_WINDOWS = 1, IC_UBUNTU = 2 };
static void Logo(Ctx *c, int x, int y, int size, DWORD col);
static void IconTile(Ctx *c, RECT r, DWORD c1, DWORD c2, const wchar_t *glyph, int drawn)
{
    int sz = r.right - r.left;
    FillRoundGrad(c, r, sz * 0.28f, c1, c2, 2);
    FillRound(c, Rc(r.left, r.top, sz, sz / 2), sz * 0.28f, 0x14FFFFFF, 0, 0);   /* glans */
    if (drawn == IC_WINDOWS) Logo(c, r.left + sz * 27 / 100, r.top + sz * 27 / 100, sz * 46 / 100, 0xFFFFFFFF);
    else if (drawn == IC_UBUNTU)
    {
        GpPen *p; GdipCreatePen1(0xFFFFFFFF, sz * 0.09f, 2, &p);
        float cx = r.left + sz / 2.0f, cy = r.top + sz / 2.0f, rr = sz * 0.2f;
        GdipDrawEllipse(c->g, p, cx - rr, cy - rr, rr * 2, rr * 2); GdipDeletePen(p);
        GpBrush *w; GdipCreateSolidFill(0xFFFFFFFF, &w);
        float dots[3][2] = { { 0, -1 }, { 0.866f, 0.5f }, { -0.866f, 0.5f } }, d = sz * 0.13f;
        for (int i = 0; i < 3; i++) GdipFillEllipse(c->g, w, cx + dots[i][0] * rr * 1.25f - d / 2, cy + dots[i][1] * rr * 1.25f - d / 2, d, d);
        GdipDeleteBrush(w);
    }
    else if (glyph && glyph[0] < 0x2000 && glyph[1] == 0) Text(c, f_h2, RGB(255, 255, 255), r, glyph, DT_CENTER | DT_VCENTER | DT_SINGLELINE);   /* bogstav */
    else Text(c, sz > S(40) ? f_icon : f_iconS, RGB(255, 255, 255), r, glyph, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
}

static void Keycap(Ctx *c, int x, int y, const wchar_t *k)
{
    RECT r = Rc(x, y, S(26), S(26));
    FillRound(c, r, S(6), 0x14FFFFFF, 0xFF3A4A6E, S(1.2f));
    Text(c, f_key, T_MUTED, r, k, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
}

static void Chevron(Ctx *c, int x, int cy, DWORD col)
{
    GpPen *p; GdipCreatePen1(col, S(2.2f), 2, &p); GdipSetPenStartCap(p, 2); GdipSetPenEndCap(p, 2);
    GdipDrawLine(c->g, p, (float)x, (float)(cy - S(7)), (float)(x + S(7)), (float)cy);
    GdipDrawLine(c->g, p, (float)(x + S(7)), (float)cy, (float)x, (float)(cy + S(7)));
    GdipDeletePen(p);
}

/* Trin øverst: Log ind → Vælg → Navn og bruger → Installér */
static void Stepper(Ctx *c, int cx, int y, int step)
{
    static const wchar_t *names[] = { L"Log ind", L"Vælg", L"Navn og bruger", L"Installér" };
    int gap = S(150), x0 = cx - gap * 3 / 2;
    GpPen *line; GdipCreatePen1(0xFF2B3A5C, S(2), 2, &line);
    GpPen *done; GdipCreatePen1(C_ACCENT, S(2), 2, &done);
    for (int i = 0; i < 3; i++) GdipDrawLine(c->g, i < step ? done : line, (float)(x0 + i * gap + S(9)), (float)(y + S(8)), (float)(x0 + (i + 1) * gap - S(9)), (float)(y + S(8)));
    GdipDeletePen(line); GdipDeletePen(done);
    for (int i = 0; i < 4; i++)
    {
        int x = x0 + i * gap; float d = (float)S(16);
        GpBrush *b; GdipCreateSolidFill(i < step ? C_ACCENT : i == step ? 0xFFFFFFFF : 0xFF2B3A5C, &b);
        if (i == step) Glow(c, (float)x, (float)(y + S(8)), (float)S(20), (float)S(20), 0x663B82F6);
        GdipFillEllipse(c->g, b, x - d / 2, y + S(8) - d / 2, d, d); GdipDeleteBrush(b);
        if (i < step) Text(c, f_key, RGB(255, 255, 255), Rc(x - S(8), y, S(16), S(16)), L"\x2713", DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        Text(c, f_small, i == step ? T_WHITE : T_DIM, Rc(x - S(80), y + S(22), S(160), S(20)), names[i], DT_CENTER | DT_SINGLELINE);
    }
}

/* Række med til/fra-knap: automatisk diskvalg */
static void DiskRow(Ctx *c, RECT r)
{
    BOOL hov = g_hover == B_AUTODISK, on = g_autoDisk && g_ndisks, can = g_ndisks > 0;
    FillRound(c, r, S(14), on ? 0x1AF59E0B : (hov ? C_CARDHI : 0x0AFFFFFF), on ? 0x99F59E0B : (hov ? 0xFF3A4F7A : C_BORDER), S(1.2f));
    int h = r.bottom - r.top, ic = S(36);
    IconTile(c, Rc(r.left + S(16), r.top + (h - ic) / 2, ic, ic), on ? 0xFFF59E0B : 0xFF475569, on ? 0xFFB45309 : 0xFF334155, L"\xD83D\xDCBE", 0);
    wchar_t t[200], d[220];
    if (!can) { wcscpy(t, L"Ingen disk fundet"); wcscpy(d, L"Windows Setup viser diskene – eller indlæs en driver der."); }
    else if (on) { wcscpy(t, L"Disk vælges automatisk – hele disk 0 slettes"); swprintf(d, 220, L"%s%s", g_disk0, g_ndisks > 1 ? L"  ·  der er flere diske, tjek at det er den rigtige" : L""); }
    else { wcscpy(t, L"Du vælger disk i Windows Setup"); swprintf(d, 220, L"Slå til for at installere på disk 0 (%s) uden at blive spurgt", g_disk0); }
    int tx = r.left + S(16) + ic + S(14), tw = r.right - tx - S(90);
    Text(c, f_btn, T_WHITE, Rc(tx, r.top + S(9), tw, S(24)), t, DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS);
    Text(c, f_small, on ? RGB(253, 230, 138) : T_MUTED, Rc(tx, r.top + S(33), tw, S(20)), d, DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS);
    if (!can) return;
    /* kontakt */
    RECT sw = Rc(r.right - S(16) - S(52), r.top + (h - S(28)) / 2, S(52), S(28));
    FillRound(c, sw, S(14), on ? 0xFFF59E0B : 0xFF334155, 0, 0);
    GpBrush *b; GdipCreateSolidFill(0xFFFFFFFF, &b);
    float kx = on ? (float)(sw.right - S(25)) : (float)(sw.left + S(3));
    GdipFillEllipse(c->g, b, kx, (float)(sw.top + S(3)), (float)S(22), (float)S(22)); GdipDeleteBrush(b);
    SetHit(r, B_AUTODISK);
}

/* Liste med flueben: "Det sker" */
static int Checklist(Ctx *c, int x, int y, int w, const wchar_t **items, int n)
{
    for (int i = 0; i < n; i++, y += S(30))
    {
        GpBrush *b; GdipCreateSolidFill(0x2234D399, &b); GdipFillEllipse(c->g, b, (float)x, (float)(y + S(2)), (float)S(20), (float)S(20)); GdipDeleteBrush(b);
        Text(c, f_key, RGBOF(C_GREEN), Rc(x, y + S(2), S(20), S(20)), L"\x2713", DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        Text(c, f_body, T_WHITE, Rc(x + S(32), y, w - S(32), S(26)), items[i], DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
    }
    return y;
}

static void Field(Ctx *c, RECT r, const wchar_t *label, HWND edit, RECT *store)
{
    Text(c, f_small, T_MUTED, Rc(r.left, r.top - S(26), r.right - r.left, S(22)), label, DT_LEFT | DT_SINGLELINE);
    BOOL focus = GetFocus() == edit;
    FillRound(c, r, S(9), C_FIELD, focus ? C_ACCENT : C_BORDER, focus ? S(2) : S(1.2f));
    *store = r;
}

/* Vinduets logo: fire felter (Windows) */
static void Logo(Ctx *c, int x, int y, int size, DWORD col)
{
    GpBrush *b; GdipCreateSolidFill(col, &b);
    float g = size * 0.08f, h = (size - g) / 2;
    GdipFillRectangle(c->g, b, (float)x, (float)y, h, h);
    GdipFillRectangle(c->g, b, x + h + g, (float)y, h, h);
    GdipFillRectangle(c->g, b, (float)x, y + h + g, h, h);
    GdipFillRectangle(c->g, b, x + h + g, y + h + g, h, h);
    GdipDeleteBrush(b);
}

static void Spinner(Ctx *c, int cx, int cy, int r)
{
    GpPen *p; GdipCreatePen1(0x33FFFFFF, S(5), 2, &p);
    GdipDrawEllipse(c->g, p, (float)(cx - r), (float)(cy - r), (float)(r * 2), (float)(r * 2)); GdipDeletePen(p);
    GdipCreatePen1(C_ACCHI, S(5), 2, &p); GdipSetPenStartCap(p, 2); GdipSetPenEndCap(p, 2);
    GdipDrawArc(c->g, p, (float)(cx - r), (float)(cy - r), (float)(r * 2), (float)(r * 2), (float)((g_tick * 9) % 360), 100);
    GdipDeletePen(p);
}

/* Et valg-kort: ikon-cirkel, titel, beskrivelse */
static void Card(Ctx *c, RECT r, int id, const wchar_t *key, const wchar_t *title, const wchar_t *desc, DWORD c1, DWORD c2,
                 const wchar_t *glyph, int drawn)
{
    BOOL hov = g_hover == id;
    if (hov) Glow(c, (float)(r.left + (r.right - r.left) / 2), (float)(r.top + (r.bottom - r.top) / 2), (float)((r.right - r.left) * 0.6f), (float)((r.bottom - r.top) * 1.2f), (c1 & 0x00FFFFFF) | 0x22000000);
    FillRound(c, r, S(16), hov ? C_CARDHI : C_CARD, hov ? ((c1 & 0x00FFFFFF) | 0xCC000000) : C_BORDER, hov ? S(1.8f) : S(1.2f));
    int pad = S(20), ic = S(50);
    RECT ir = Rc(r.left + pad, r.top + (r.bottom - r.top - ic) / 2, ic, ic);
    IconTile(c, ir, c1, c2, glyph, drawn);
    int right = r.right - pad;
    Chevron(c, right - S(8), (r.top + r.bottom) / 2, hov ? 0xFFFFFFFF : 0xFF64748B);
    if (key) Keycap(c, right - S(8) - S(44), (r.top + r.bottom) / 2 - S(13), key);
    int tx = ir.right + S(18), tw = right - S(64) - tx;
    int th = TextHeight(c, f_btn, tw, title), dh = TextHeight(c, f_small, tw, desc), gap = S(4);
    int ty = r.top + (r.bottom - r.top - th - gap - dh) / 2;
    Text(c, f_btn, T_WHITE, Rc(tx, ty, tw, th), title, DT_LEFT | DT_WORDBREAK);
    Text(c, f_small, T_MUTED, Rc(tx, ty + th + gap, tw, dh), desc, DT_LEFT | DT_WORDBREAK);
    SetHit(r, id);
}

static void PlaceEdit(HWND e, RECT r, BOOL show)
{
    if (!show) { if (IsWindowVisible(e)) ShowWindow(e, SW_HIDE); return; }
    int h = S(28), pad = S(14);
    RECT want = Rc(r.left + pad, r.top + (r.bottom - r.top - h) / 2, r.right - r.left - pad * 2, h), cur;
    GetWindowRect(e, &cur); MapWindowPoints(NULL, g_wnd, (POINT *)&cur, 2);
    if (memcmp(&cur, &want, sizeof want)) MoveWindow(e, want.left, want.top, want.right - want.left, want.bottom - want.top, TRUE);
    if (!IsWindowVisible(e)) ShowWindow(e, SW_SHOW);
}

static const wchar_t *ModeTitle(int m)
{
    return m == M_CLEAN ? L"Windows 11 – helt ren" : m == M_MANAGED ? L"Windows 11 med mine ting" : L"Windows 11 med profil";
}

static void Paint(HDC hdc)
{
    RECT cr; GetClientRect(g_wnd, &cr);
    int W = cr.right, H = cr.bottom;
    HDC dc = CreateCompatibleDC(hdc);
    HBITMAP bmp = CreateCompatibleBitmap(hdc, W, H);
    HGDIOBJ old = SelectObject(dc, bmp);
    Ctx c = { dc, NULL };
    GdipCreateFromHDC(dc, &c.g);
    GdipSetSmoothingMode(c.g, 4);
    g_nhits = 0;

    /* baggrund: mørk gradient med bløde farvede lys */
    GpRectF all = { 0, 0, (float)W, (float)H };
    GpBrush *bg; GdipCreateLineBrushFromRect(&all, C_BG1, C_BG2, 1, 0, &bg);
    GdipFillRectangle(c.g, bg, 0, 0, (float)W, (float)H); GdipDeleteBrush(bg);
    Glow(&c, W * 0.50f, H * 0.52f, W * 0.55f, H * 0.62f, 0x402563EB);
    Glow(&c, W * 0.86f, H * 0.10f, W * 0.34f, H * 0.42f, 0x337C3AED);
    Glow(&c, W * 0.08f, H * 0.95f, W * 0.36f, H * 0.40f, 0x2A0EA5E9);

    /* top: logo + titel + trin + netværk */
    int top = S(40), lx = S(48);
    IconTile(&c, Rc(lx, top, S(44), S(44)), 0xFF3B82F6, 0xFF6366F1, NULL, IC_WINDOWS);
    Text(&c, f_h2, T_WHITE, Rc(lx + S(58), top - S(1), S(400), S(28)), L"WinSetup", DT_LEFT | DT_SINGLELINE);
    Text(&c, f_small, T_MUTED, Rc(lx + S(58), top + S(25), S(400), S(20)), g_label, DT_LEFT | DT_SINGLELINE);
    int step = g_screen == SC_LOGIN ? 0 : g_screen == SC_DETAILS ? 2 : g_screen == SC_RUNNING ? 3 :
               (g_screen == SC_DEPLOY || g_screen == SC_CHOOSE || g_screen == SC_PROFILE || g_screen == SC_UBUNTU) ? 1 : -1;
    if (step >= 0 && W >= S(1100)) Stepper(&c, W / 2, top + S(4), step);
    if (g_ip[0])
    {
        RECT pill = Rc(W - S(48) - S(250), top + S(6), S(250), S(32));
        FillRound(&c, pill, S(16), 0x14FFFFFF, 0xFF2B3A5C, S(1));
        GpBrush *gd; GdipCreateSolidFill(C_GREEN, &gd); GdipFillEllipse(c.g, gd, (float)(pill.left + S(14)), (float)(pill.top + S(12)), (float)S(8), (float)S(8)); GdipDeleteBrush(gd);
        wchar_t net[128]; swprintf(net, 128, L"%s  ·  %s", g_ip, g_mac);
        Text(&c, f_small, T_MUTED, Rc(pill.left + S(30), pill.top, S(214), S(32)), net, DT_LEFT | DT_VCENTER | DT_SINGLELINE);
    }

    /* panel */
    int pw = W - S(120); if (pw > S(900)) pw = S(900);
    int px = (W - pw) / 2, py = S(128), inner = pw - S(96), ix = px + S(48);
    int y = py + S(44);
    BOOL showUser = FALSE, showPass = FALSE, showPc = FALSE, showWin = FALSE;
    int panelH = 0;

    /* panelets højde beregnes pr. skærm, så det kan tegnes før indholdet */
    switch (g_screen)
    {
        case SC_NET: case SC_RUNNING: panelH = S(380); break;
        case SC_LOGIN: panelH = S(480); break;
        case SC_DEPLOY: panelH = S(580); break;
        case SC_CHOOSE: panelH = S(560); break;
        case SC_PROFILE: { int rows = g_nprof ? g_nprof : 1; panelH = S(250) + rows * S(78); if (panelH > H - S(200)) panelH = H - S(200); break; }
        case SC_DETAILS: panelH = S(600); break;
        case SC_UBUNTU: panelH = S(530); break;
        default: panelH = S(380); break;
    }
    if (py + panelH > H - S(76)) py = (H - panelH) / 2, y = py + S(44);
    RECT panel = Rc(px, py, pw, panelH);
    FillRound(&c, Rc(px, py + S(14), pw, panelH), S(26), 0x50000000, 0, 0);   /* skygge */
    FillRoundGrad(&c, panel, S(24), 0xF2142038, 0xF20E1628, 1);
    FillRound(&c, panel, S(24), 0, 0x30FFFFFF, S(1.2f));                        /* glaskant */

    switch (g_screen)
    {
    case SC_NET:
        Glow(&c, (float)(px + pw / 2), (float)(y + S(84)), (float)S(90), (float)S(90), 0x553B82F6);
        Spinner(&c, px + pw / 2, y + S(84), S(36));
        Text(&c, f_h2, T_WHITE, Rc(ix, y + S(150), inner, S(32)), g_netWait < 90 ? L"Forbinder til netværket …" : L"Intet netværk", DT_CENTER | DT_SINGLELINE);
        Text(&c, f_body, T_MUTED, Rc(ix, y + S(190), inner, S(60)),
             g_netWait < 90 ? L"Venter på en IP-adresse fra routeren." : L"Tjek netkablet. Netkortet skal være tilsluttet samme netværk som serveren.",
             DT_CENTER | DT_WORDBREAK);
        if (g_netWait >= 90) Button(&c, Rc(px + pw / 2 - S(90), y + S(260), S(180), S(50)), B_RETRY, L"Prøv igen", TRUE);
        break;

    case SC_LOGIN:
    {
        IconTile(&c, Rc(ix, y, S(56), S(56)), 0xFF0EA5E9, 0xFF2563EB, L"\xD83D\xDD12", 0);
        Text(&c, f_title, T_WHITE, Rc(ix + S(76), y - S(8), inner - S(76), S(46)), L"Log ind på Unraid", DT_LEFT | DT_SINGLELINE);
        wchar_t sub[200]; swprintf(sub, 200, L"Samme bruger som du bruger til \\\\%s\\%s", g_server, g_share);
        Text(&c, f_body, T_MUTED, Rc(ix + S(76), y + S(40), inner - S(76), S(26)), sub, DT_LEFT | DT_SINGLELINE);
        int fy = y + S(130), fh = S(54);
        Field(&c, Rc(ix, fy, inner, fh), L"Unraid-bruger", g_eUser, &g_rUser);
        Field(&c, Rc(ix, fy + S(100), inner, fh), L"Adgangskode", g_ePass, &g_rPass);
        showUser = showPass = TRUE;
        if (g_err[0]) Text(&c, f_body, RGBOF(C_RED), Rc(ix, fy + S(166), inner, S(26)), g_err, DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS);
        if (g_busy) Spinner(&c, px + pw - S(48) - S(200) - S(36), py + panelH - S(66), S(14));
        Button(&c, Rc(px + pw - S(48) - S(190), py + panelH - S(92), S(190), S(52)), B_LOGIN, g_busy ? L"Logger ind …" : L"Log ind", TRUE);
        break;
    }

    case SC_DEPLOY:
    {
        RECT badge = Rc(ix, y, S(214), S(28));
        FillRound(&c, badge, S(14), 0x2234D399, 0x6634D399, S(1));
        Text(&c, f_key, RGBOF(C_GREEN), badge, L"\x2713  KLAR FRA FLEETDECK", DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        Text(&c, f_title, T_WHITE, Rc(ix, y + S(40), inner, S(46)), L"Installationen er lagt klar", DT_LEFT | DT_SINGLELINE);
        int m = !wcscmp(D_MODE, L"clean") ? M_CLEAN : !wcscmp(D_MODE, L"profile") ? M_PROFILE : M_MANAGED;
        RECT box = Rc(ix, y + S(112), inner, S(160));
        Glow(&c, (float)(box.left + S(60)), (float)(box.top + S(70)), (float)S(160), (float)S(110), 0x263B82F6);
        FillRound(&c, box, S(18), C_CARD, C_BORDER, S(1.2f));
        DWORD a1 = m == M_CLEAN ? 0xFF64748B : m == M_PROFILE ? 0xFFA855F7 : 0xFF3B82F6, a2 = m == M_CLEAN ? 0xFF334155 : m == M_PROFILE ? 0xFF6D28D9 : 0xFF6366F1;
        IconTile(&c, Rc(box.left + S(26), box.top + S(28), S(64), S(64)), a1, a2, m == M_PROFILE ? L"\xD83D\xDC64" : m == M_MANAGED ? L"\x2605" : NULL, m == M_CLEAN ? IC_WINDOWS : 0);
        wchar_t what[200];
        if (m == M_PROFILE) swprintf(what, 200, L"Windows 11 med profilen \"%s\"", D_PROFNAME); else wcscpy(what, ModeTitle(m));
        Text(&c, f_h2, T_WHITE, Rc(box.left + S(112), box.top + S(28), inner - S(130), S(30)), what, DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS);
        /* pc-navn og bruger som "piller" */
        wchar_t pcs[80], us[80]; swprintf(pcs, 80, L"\xD83D\xDDA5  %s", D_PC); swprintf(us, 80, L"\xD83D\xDC64  %s", D_USER);
        RECT p1 = Rc(box.left + S(112), box.top + S(78), S(200), S(36)), p2 = Rc(p1.right + S(12), p1.top, S(240), S(36));
        FillRound(&c, p1, S(18), 0x14FFFFFF, C_BORDER, S(1)); FillRound(&c, p2, S(18), 0x14FFFFFF, C_BORDER, S(1));
        Text(&c, f_body, T_WHITE, p1, pcs, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
        Text(&c, f_body, T_WHITE, p2, us, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
        DiskRow(&c, Rc(ix, box.bottom + S(16), inner, S(62)));
        Button(&c, Rc(px + pw - S(48) - S(230), py + panelH - S(92), S(230), S(52)), B_START, L"Start installation", TRUE);
        Button(&c, Rc(px + pw - S(48) - S(230) - S(16) - S(150), py + panelH - S(92), S(150), S(52)), B_CHOOSE_SELF, L"Vælg selv", FALSE);
        break;
    }

    case SC_CHOOSE:
    {
        Text(&c, f_title, T_WHITE, Rc(ix, y, inner, S(46)), L"Hvad skal installeres?", DT_LEFT | DT_SINGLELINE);
        Text(&c, f_body, T_MUTED, Rc(ix, y + S(52), inner, S(26)), L"Klik på et valg – eller tryk på tasten ud for det.", DT_LEFT | DT_SINGLELINE);
        int cy = y + S(100), ch = S(88), gap = S(14);
        wchar_t d2[200];
        swprintf(d2, 200, L"Programmer og spil, dine filer, baggrund og opsætning%s%s", g_defProfName[0] ? L" · " : L"", g_defProfName);
        Card(&c, Rc(ix, cy, inner, ch), B_MODE1, L"1", L"Windows 11 – helt ren", L"Kun Windows. Ingen programmer og ingen styring fra Fleetdeck.",
             0xFF64748B, 0xFF334155, NULL, IC_WINDOWS);
        Card(&c, Rc(ix, cy + ch + gap, inner, ch), B_MODE2, L"2", L"Windows 11 med mine ting", d2, 0xFF3B82F6, 0xFF6366F1, L"\x2605", 0);
        Card(&c, Rc(ix, cy + (ch + gap) * 2, inner, ch), B_MODE3, L"3", L"Windows 11 – vælg profil", L"Fx en børne-pc: programmer og indstillinger fra en profil i Fleetdeck.",
             0xFFA855F7, 0xFF6D28D9, L"\xD83D\xDC64", 0);
        RECT ub = Rc(ix, cy + (ch + gap) * 3, inner, S(60));
        BOOL hov = g_hover == B_UBUNTU;
        FillRound(&c, ub, S(14), hov ? C_CARDHI : 0x0AFFFFFF, hov ? 0xCCE95420 : C_BORDER, S(1.2f));
        IconTile(&c, Rc(ub.left + S(20), ub.top + S(13), S(34), S(34)), 0xFFF97316, 0xFFC2410C, NULL, IC_UBUNTU);
        Text(&c, f_btn, T_WHITE, Rc(ub.left + S(70), ub.top, S(200), S(60)), L"Ubuntu Linux", DT_LEFT | DT_VCENTER | DT_SINGLELINE);
        Text(&c, f_small, T_MUTED, Rc(ub.left + S(196), ub.top, inner - S(260), S(60)), L"Desktop eller Server – startes fra Fleetdeck", DT_LEFT | DT_VCENTER | DT_SINGLELINE);
        Chevron(&c, ub.right - S(28), (ub.top + ub.bottom) / 2, hov ? 0xFFFFFFFF : 0xFF64748B);
        SetHit(ub, B_UBUNTU);
        break;
    }

    case SC_PROFILE:
    {
        Text(&c, f_title, T_WHITE, Rc(ix, y, inner, S(46)), L"Vælg profil", DT_LEFT | DT_SINGLELINE);
        Text(&c, f_body, T_MUTED, Rc(ix, y + S(52), inner, S(26)), L"Profilerne kommer fra Fleetdeck – programmer, spil og opdateringer følger med.", DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS);
        int ry = y + S(100), rh = S(64);
        if (!g_nprof) Text(&c, f_body, RGBOF(C_AMBER), Rc(ix, ry, inner, S(26)), L"Ingen profiler fundet. Opret en i Fleetdeck under Profiler.", DT_LEFT | DT_SINGLELINE);
        static const DWORD pal[][2] = { { 0xFF3B82F6, 0xFF6366F1 }, { 0xFFA855F7, 0xFF6D28D9 }, { 0xFF10B981, 0xFF047857 }, { 0xFFF59E0B, 0xFFB45309 }, { 0xFFEC4899, 0xFFBE185D } };
        for (int i = 0; i < g_nprof && ry + rh < py + panelH - S(100); i++, ry += rh + S(12))
        {
            RECT r = Rc(ix, ry, inner, rh);
            BOOL hov = g_hover == B_PROF0 + i;
            FillRound(&c, r, S(14), hov ? C_CARDHI : C_CARD, hov ? ((pal[i % 5][0] & 0x00FFFFFF) | 0xCC000000) : C_BORDER, S(1.3f));
            wchar_t ini[3] = { g_prof[i].name[0] ? (wchar_t)towupper(g_prof[i].name[0]) : L'?', 0 };
            IconTile(&c, Rc(r.left + S(14), r.top + S(12), S(40), S(40)), pal[i % 5][0], pal[i % 5][1], ini, 0);
            Text(&c, f_btn, T_WHITE, Rc(r.left + S(70), r.top, inner - S(160), rh), g_prof[i].name, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
            if (i < 9) { wchar_t k[4]; swprintf(k, 4, L"%d", i + 1); Keycap(&c, r.right - S(80), r.top + (rh - S(26)) / 2, k); }
            Chevron(&c, r.right - S(30), (r.top + r.bottom) / 2, hov ? 0xFFFFFFFF : 0xFF64748B);
            SetHit(r, B_PROF0 + i);
        }
        Button(&c, Rc(ix, py + panelH - S(92), S(140), S(52)), B_BACK, L"Tilbage", FALSE);
        break;
    }

    case SC_DETAILS:
    {
        wchar_t t[200];
        if (g_mode == M_PROFILE && g_profSel >= 0) swprintf(t, 200, L"Profil: %s", g_prof[g_profSel].name); else wcscpy(t, ModeTitle(g_mode));
        RECT badge = Rc(ix, y, S(16) + (int)wcslen(t) * S(8), S(28));
        if (badge.right > ix + inner) badge.right = ix + inner;
        FillRound(&c, badge, S(14), 0x223B82F6, 0x663B82F6, S(1));
        Text(&c, f_key, RGBOF(C_ACCHI), badge, t, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
        Text(&c, f_title, T_WHITE, Rc(ix, y + S(40), inner, S(46)), L"Navn og bruger", DT_LEFT | DT_SINGLELINE);
        Text(&c, f_body, T_MUTED, Rc(ix, y + S(90), inner, S(26)), L"Samme pc-navn som før = Fleetdeck genkender pc'en og beholder profil og historik.", DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS);
        int fy = y + S(156), fh = S(54), half = (inner - S(24)) / 2;
        Field(&c, Rc(ix, fy, half, fh), L"Pc-navn  ·  højst 15 tegn: A-Z, 0-9 og -", g_ePc, &g_rPc);
        Field(&c, Rc(ix + half + S(24), fy, half, fh), L"Windows-bruger  ·  adgangskode ved første login", g_eWin, &g_rWin);
        showPc = showWin = TRUE;
        int ly = fy + fh + S(20);
        DiskRow(&c, Rc(ix, ly, inner, S(62)));
        ly += S(80);
        if (g_err[0])
        {
            RECT er = Rc(ix, ly, inner, S(44));
            FillRound(&c, er, S(12), 0x22F87171, 0x66F87171, S(1));
            Text(&c, f_body, RGBOF(C_RED), Rc(ix + S(16), ly, inner - S(32), S(44)), g_err, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
        }
        else
        {
            static const wchar_t *clean[] = { L"Windows 11 Pro på dansk", L"Din konto – adgangskoden vælges ved første login", L"Ingen programmer og ingen styring" };
            static const wchar_t *mine[] = { L"Windows 11 Pro på dansk med drivere", L"Programmer og spil fra profilen, dine filer, baggrund og opsætning", L"Windows Update og genstart – helt af sig selv" };
            static const wchar_t *prof[] = { L"Windows 11 Pro på dansk med drivere", L"Programmer og spil fra den valgte profil", L"Opdateringer styres fra Fleetdeck" };
            Text(&c, f_small, T_DIM, Rc(ix, ly, inner, S(20)), L"DET SKER", DT_LEFT | DT_SINGLELINE);
            Checklist(&c, ix, ly + S(26), inner, g_mode == M_CLEAN ? clean : g_mode == M_PROFILE ? prof : mine, 3);
        }
        Button(&c, Rc(ix, py + panelH - S(92), S(140), S(52)), B_BACK, L"Tilbage", FALSE);
        Button(&c, Rc(px + pw - S(48) - S(230), py + panelH - S(92), S(230), S(52)), B_INSTALL, L"Installér Windows", TRUE);
        break;
    }

    case SC_UBUNTU:
    {
        IconTile(&c, Rc(ix, y, S(56), S(56)), 0xFFF97316, 0xFFC2410C, NULL, IC_UBUNTU);
        Text(&c, f_title, T_WHITE, Rc(ix + S(76), y - S(8), inner - S(76), S(46)), L"Ubuntu Linux", DT_LEFT | DT_SINGLELINE);
        Text(&c, f_body, T_MUTED, Rc(ix + S(76), y + S(40), inner - S(76), S(26)), L"Startes fra Fleetdeck, så pc'en får Ubuntus egen bootfil.", DT_LEFT | DT_SINGLELINE);
        Text(&c, f_small, T_DIM, Rc(ix, y + S(92), inner, S(20)), L"DENNE PC'S MAC-ADRESSE", DT_LEFT | DT_SINGLELINE);
        RECT mb = Rc(ix, y + S(118), inner, S(78));
        Glow(&c, (float)(mb.left + inner / 2), (float)(mb.top + S(39)), (float)(inner * 0.55f), (float)S(90), 0x22F97316);
        FillRound(&c, mb, S(16), C_FIELD, 0xCCE95420, S(1.5f));
        Text(&c, f_mono, T_WHITE, mb, g_mac[0] ? g_mac : L"(ukendt)", DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        static const wchar_t *steps[] = { L"Åbn Fleetdeck  →  Udrulning  →  Ny udrulning  →  Ubuntu",
                                          L"Skriv MAC-adressen, værtsnavnet, brugeren og adgangskoden",
                                          L"Genstart pc'en og boot fra netværket igen" };
        for (int i = 0; i < 3; i++)
        {
            int sy = y + S(222) + i * S(36);
            wchar_t n[4]; swprintf(n, 4, L"%d", i + 1);
            RECT nr = Rc(ix, sy, S(26), S(26));
            FillRound(&c, nr, S(13), 0x33F97316, 0, 0);
            Text(&c, f_key, RGB(253, 186, 116), nr, n, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
            Text(&c, f_body, T_WHITE, Rc(ix + S(40), sy, inner - S(40), S(26)), steps[i], DT_LEFT | DT_VCENTER | DT_SINGLELINE);
        }
        Button(&c, Rc(ix, py + panelH - S(92), S(140), S(52)), B_BACK, L"Tilbage", FALSE);
        Button(&c, Rc(px + pw - S(48) - S(190), py + panelH - S(92), S(190), S(52)), B_REBOOT, L"Genstart", TRUE);
        break;
    }

    case SC_RUNNING:
        Glow(&c, (float)(px + pw / 2), (float)(y + S(80)), (float)S(90), (float)S(90), 0x5534D399);
        Spinner(&c, px + pw / 2, y + S(80), S(34));
        Text(&c, f_h2, T_WHITE, Rc(ix, y + S(140), inner, S(32)), g_test ? L"Svarfilen er lavet (testtilstand)" : L"Windows Setup starter …", DT_CENTER | DT_SINGLELINE);
        Text(&c, f_body, T_MUTED, Rc(ix, y + S(180), inner, S(26)), g_info, DT_CENTER | DT_SINGLELINE);
        Text(&c, f_small, T_DIM, Rc(ix, y + S(220), inner, S(44)), g_autoDisk && g_ndisks ? L"Disken vælges automatisk. Resten kører af sig selv, og pc'en genstarter undervejs." : L"Vælg disk i Windows Setup. Resten kører af sig selv, og pc'en genstarter undervejs.", DT_CENTER | DT_WORDBREAK);
        break;

    case SC_ERROR:
        Text(&c, f_title, RGBOF(C_RED), Rc(ix, y, inner, S(46)), L"Noget gik galt", DT_LEFT | DT_SINGLELINE);
        Text(&c, f_body, T_WHITE, Rc(ix, y + S(70), inner, S(150)), g_err, DT_LEFT | DT_WORDBREAK);
        Button(&c, Rc(px + pw - S(48) - S(180), py + panelH - S(90), S(180), S(50)), B_RETRY, L"Prøv igen", TRUE);
        break;
    }

    /* bundlinje */
    int by = H - S(58);
    Button(&c, Rc(S(40), by, S(170), S(40)), B_CMD, L"Kommandoprompt", FALSE);
    Button(&c, Rc(S(40) + S(186), by, S(130), S(40)), B_REBOOT, L"Genstart", FALSE);
    Text(&c, f_small, T_DIM, Rc(W - S(560), by, S(520), S(40)), L"Fleetdeck · netværksinstallation", DT_RIGHT | DT_VCENTER | DT_SINGLELINE);

    GdipDeleteGraphics(c.g);
    BitBlt(hdc, 0, 0, W, H, dc, 0, 0, SRCCOPY);
    SelectObject(dc, old); DeleteObject(bmp); DeleteDC(dc);

    PlaceEdit(g_eUser, g_rUser, showUser); PlaceEdit(g_ePass, g_rPass, showPass);
    PlaceEdit(g_ePc, g_rPc, showPc); PlaceEdit(g_eWin, g_rWin, showWin);
}

/* ------------------------------------------------------------------ handlinger */
static void Go(int screen)
{
    g_screen = screen; g_err[0] = 0; g_hover = B_NONE;
    if (screen == SC_LOGIN) SetFocus(g_eUser);
    else if (screen == SC_DETAILS) { SetFocus(g_ePc); SendMessageW(g_ePc, EM_SETSEL, 0, -1); }
    else SetFocus(g_wnd);
    InvalidateRect(g_wnd, NULL, FALSE);
}

static void AfterLogin(void)
{
    if (!CheckFiles()) { Go(SC_ERROR); return; }
    LoadData();
    ReadMachine(); ReadDisks();
    Go(g_hasDeploy ? SC_DEPLOY : SC_CHOOSE);
}

static void OpenDetails(int mode)
{
    g_mode = mode;
    wchar_t def[32];
    if (g_mac[0] && wcslen(g_mac) == 17) swprintf(def, 32, L"PC-%c%c%c%c%c%c", g_mac[9], g_mac[10], g_mac[12], g_mac[13], g_mac[15], g_mac[16]);
    else wcscpy(def, L"NY-PC");
    SetWindowTextW(g_ePc, def);
    SetWindowTextW(g_eWin, g_defUser);
    Go(SC_DETAILS);
}

static void Action(int id)
{
    if (g_busy) return;
    switch (id)
    {
    case B_CMD: Run(L"cmd.exe", TRUE); break;
    case B_REBOOT: if (!g_test) Run(L"wpeutil.exe reboot", FALSE); break;
    case B_RETRY: g_netWait = 0; Go(g_screen == SC_ERROR && g_ip[0] ? SC_LOGIN : SC_NET); break;
    case B_LOGIN:
        GetWindowTextW(g_eUser, g_loginUser, 128); GetWindowTextW(g_ePass, g_loginPass, 128);
        if (!g_loginUser[0]) { wcscpy(g_err, L"Skriv din Unraid-bruger."); SetFocus(g_eUser); InvalidateRect(g_wnd, NULL, FALSE); break; }
        g_busy = 1; g_err[0] = 0; InvalidateRect(g_wnd, NULL, FALSE);
        CreateThread(NULL, 0, LoginThread, NULL, 0, NULL);
        break;
    case B_START:
        StartInstall(D_PC, D_USER, D_MODE, D_PROFILE, D_ID); InvalidateRect(g_wnd, NULL, FALSE); break;
    case B_CHOOSE_SELF: Go(SC_CHOOSE); break;
    case B_AUTODISK: if (g_ndisks) { g_autoDisk = !g_autoDisk; InvalidateRect(g_wnd, NULL, FALSE); } break;
    case B_MODE1: OpenDetails(M_CLEAN); break;
    case B_MODE2: OpenDetails(M_MANAGED); break;
    case B_MODE3: g_profSel = -1; Go(SC_PROFILE); break;
    case B_UBUNTU: Go(SC_UBUNTU); break;
    case B_BACK: Go(g_screen == SC_DETAILS && g_mode == M_PROFILE ? SC_PROFILE : SC_CHOOSE); break;
    case B_INSTALL:
    {
        wchar_t pc[64], user[64];
        GetWindowTextW(g_ePc, pc, 64); GetWindowTextW(g_eWin, user, 64);
        if (!ValidPc(pc)) { wcscpy(g_err, L"Pc-navnet må have 1–15 tegn: A-Z, 0-9 og bindestreg (ikke først)."); SetFocus(g_ePc); InvalidateRect(g_wnd, NULL, FALSE); break; }
        if (!ValidUser(user)) { wcscpy(g_err, L"Brugernavnet må have 1–20 tegn: a-z, tal, mellemrum, punktum, - og _ (ingen æøå)."); SetFocus(g_eWin); InvalidateRect(g_wnd, NULL, FALSE); break; }
        StartInstall(pc, user, g_mode == M_CLEAN ? L"clean" : g_mode == M_PROFILE ? L"profile" : L"managed",
                     g_mode == M_PROFILE && g_profSel >= 0 ? g_prof[g_profSel].id : L"", L"");
        InvalidateRect(g_wnd, NULL, FALSE);
        break;
    }
    default:
        if (id >= B_PROF0 && id < B_PROF0 + g_nprof) { g_profSel = id - B_PROF0; OpenDetails(M_PROFILE); }
    }
}

static int Primary(void)
{
    switch (g_screen)
    {
    case SC_LOGIN: return B_LOGIN;
    case SC_DEPLOY: return B_START;
    case SC_CHOOSE: return B_MODE2;
    case SC_DETAILS: return B_INSTALL;
    case SC_ERROR: return B_RETRY;
    default: return B_NONE;
    }
}

/* ------------------------------------------------------------------ tekstfelter: Enter, Tab og Esc */
static WNDPROC g_editProc;
static LRESULT CALLBACK EditProc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    if (m == WM_KEYDOWN && w == VK_RETURN) { PostMessageW(g_wnd, WM_PRIMARY, 0, 0); return 0; }
    if (m == WM_KEYDOWN && w == VK_ESCAPE) { PostMessageW(g_wnd, WM_BACKKEY, 0, 0); return 0; }
    if (m == WM_KEYDOWN && w == VK_TAB)
    {
        HWND next = h == g_eUser ? g_ePass : h == g_ePass ? g_eUser : h == g_ePc ? g_eWin : g_ePc;
        SetFocus(next); SendMessageW(next, EM_SETSEL, 0, -1); InvalidateRect(g_wnd, NULL, FALSE);
        return 0;
    }
    if (m == WM_CHAR && (w == L'\r' || w == L'\t' || w == 27)) return 0;
    if (m == WM_SETFOCUS || m == WM_KILLFOCUS) InvalidateRect(g_wnd, NULL, FALSE);
    if (m == WM_CHAR && (h == g_ePc || h == g_eWin) && g_err[0]) { g_err[0] = 0; InvalidateRect(g_wnd, NULL, FALSE); }
    return CallWindowProcW(g_editProc, h, m, w, l);
}

static HWND MkEdit(DWORD extra)
{
    HWND e = CreateWindowExW(0, L"EDIT", L"", WS_CHILD | ES_AUTOHSCROLL | extra, 0, 0, 10, 10, g_wnd, NULL, GetModuleHandleW(NULL), NULL);
    SendMessageW(e, WM_SETFONT, (WPARAM)f_edit, TRUE);
    SendMessageW(e, EM_SETMARGINS, EC_LEFTMARGIN | EC_RIGHTMARGIN, 0);
    g_editProc = (WNDPROC)SetWindowLongPtrW(e, GWLP_WNDPROC, (LONG_PTR)EditProc);
    return e;
}

/* ------------------------------------------------------------------ testbilleder */
static void SaveShot(const wchar_t *name)
{
    RECT cr; GetClientRect(g_wnd, &cr);
    HDC wdc = GetDC(g_wnd), dc = CreateCompatibleDC(wdc);
    BITMAPINFO bi = { 0 };
    bi.bmiHeader.biSize = sizeof bi.bmiHeader; bi.bmiHeader.biWidth = cr.right; bi.bmiHeader.biHeight = -cr.bottom;
    bi.bmiHeader.biPlanes = 1; bi.bmiHeader.biBitCount = 24; bi.bmiHeader.biCompression = BI_RGB;
    void *bits; HBITMAP bmp = CreateDIBSection(wdc, &bi, DIB_RGB_COLORS, &bits, NULL, 0);
    HGDIOBJ old = SelectObject(dc, bmp);
    BitBlt(dc, 0, 0, cr.right, cr.bottom, wdc, 0, 0, SRCCOPY);
    int stride = ((cr.right * 3 + 3) & ~3), size = stride * cr.bottom;
    BITMAPFILEHEADER fh = { 0x4D42, sizeof fh + sizeof bi.bmiHeader + size, 0, 0, sizeof fh + sizeof bi.bmiHeader };
    wchar_t p[MAX_PATH]; swprintf(p, MAX_PATH, L"%s\\%s.bmp", g_shots, name);
    HANDLE h = CreateFileW(p, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
    DWORD w; bi.bmiHeader.biHeight = -cr.bottom;
    WriteFile(h, &fh, sizeof fh, &w, NULL); WriteFile(h, &bi.bmiHeader, sizeof bi.bmiHeader, &w, NULL); WriteFile(h, bits, size, &w, NULL);
    CloseHandle(h);
    SelectObject(dc, old); DeleteObject(bmp); DeleteDC(dc); ReleaseDC(g_wnd, wdc);
}

static void Shots(void)
{
    struct { int sc; const wchar_t *n; } list[] = {
        { SC_NET, L"1-net" }, { SC_LOGIN, L"2-login" }, { SC_DEPLOY, L"3-deploy" }, { SC_CHOOSE, L"4-choose" },
        { SC_PROFILE, L"5-profile" }, { SC_DETAILS, L"6-details" }, { SC_UBUNTU, L"7-ubuntu" }, { SC_RUNNING, L"8-running" },
    };
    LoadData(); ReadMachine(); ReadDisks(); g_autoDisk = TRUE;
    if (!g_hasDeploy) { wcscpy(D_MODE, L"profile"); wcscpy(D_PROFNAME, L"Børne-pc"); wcscpy(D_PC, L"BOERNE-PC"); wcscpy(D_USER, L"Alma"); }
    SetWindowTextW(g_eUser, L"anna"); SetWindowTextW(g_ePass, L"xxxxxxxx");
    for (int i = 0; i < (int)(sizeof list / sizeof list[0]); i++)
    {
        g_screen = list[i].sc;
        if (g_screen == SC_DETAILS) { g_profSel = g_nprof ? 0 : -1; g_mode = g_nprof ? M_PROFILE : M_MANAGED; SetWindowTextW(g_ePc, L"TEST-VM"); SetWindowTextW(g_eWin, g_defUser); }
        if (g_screen == SC_CHOOSE) g_hover = B_MODE2; else g_hover = B_NONE;
        if (g_screen == SC_RUNNING) wcscpy(g_info, L"TEST-VM  ·  bruger Test Bruger");
        g_tick = 12;
        InvalidateRect(g_wnd, NULL, TRUE); UpdateWindow(g_wnd);
        UpdateWindow(g_eUser); UpdateWindow(g_ePass); UpdateWindow(g_ePc); UpdateWindow(g_eWin);
        for (int k = 0; k < 10; k++) { MSG mm; while (PeekMessageW(&mm, NULL, 0, 0, PM_REMOVE)) { if (mm.message == WM_TIMER) continue; DispatchMessageW(&mm); } Sleep(30); }
        SaveShot(list[i].n);
    }
}

/* ------------------------------------------------------------------ vinduet */
static LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    switch (m)
    {
    case WM_PAINT: { PAINTSTRUCT ps; HDC dc = BeginPaint(h, &ps); Paint(dc); EndPaint(h, &ps); return 0; }
    case WM_ERASEBKGND: return 1;
    case WM_CTLCOLOREDIT:
        SetTextColor((HDC)w, T_WHITE); SetBkColor((HDC)w, RGBOF(C_FIELD));
        return (LRESULT)g_fieldBrush;
    case WM_MOUSEMOVE:
    {
        POINT p = { (short)LOWORD(l), (short)HIWORD(l) }; int id = B_NONE;
        for (int i = g_nhits - 1; i >= 0; i--) if (PtInRect(&g_hits[i].r, p)) { id = g_hits[i].id; break; }
        if (id != g_hover) { g_hover = id; InvalidateRect(h, NULL, FALSE); }
        SetCursor(LoadCursorW(NULL, id ? IDC_HAND : IDC_ARROW));
        return 0;
    }
    case WM_SETCURSOR: if (LOWORD(l) == HTCLIENT) return TRUE; break;
    case WM_LBUTTONUP:
    {
        POINT p = { (short)LOWORD(l), (short)HIWORD(l) };
        for (int i = g_nhits - 1; i >= 0; i--) if (PtInRect(&g_hits[i].r, p)) { Action(g_hits[i].id); break; }
        return 0;
    }
    case WM_KEYDOWN:
        if (w == VK_RETURN) Action(Primary());
        else if (w == VK_ESCAPE) PostMessageW(h, WM_BACKKEY, 0, 0);
        else if (g_screen == SC_CHOOSE && w >= '1' && w <= '3') Action(B_MODE1 + (int)(w - '1'));
        else if (g_screen == SC_PROFILE && w >= '1' && w <= '9' && (int)(w - '1') < g_nprof) Action(B_PROF0 + (int)(w - '1'));
        return 0;
    case WM_PRIMARY: Action(Primary()); return 0;
    case WM_BACKKEY:
        if (g_screen == SC_PROFILE || g_screen == SC_UBUNTU || g_screen == SC_DETAILS) Action(B_BACK);
        return 0;
    case WM_TIMER:
        if (w == 1)
        {
            g_tick++;
            if (g_screen == SC_NET && g_tick % 33 == 0 && g_netWait < 90)
            {
                g_netWait++;
                if (FindNetwork()) { if (g_test) AfterLogin(); else Go(SC_LOGIN); }
            }
            if (g_screen == SC_NET || g_screen == SC_RUNNING || g_busy) InvalidateRect(h, NULL, FALSE);
        }
        else if (w == 2) { KillTimer(h, 2); if (g_screen == SC_RUNNING) ShowWindow(h, SW_HIDE); }
        else if (w == 3)
        {
            if (SetupRunning()) { g_setupGone = 0; return 0; }
            if (++g_setupGone < 2) return 0;
            /* Setup blev lukket uden genstart (fx annulleret): kom tilbage */
            KillTimer(h, 3);
            ShowWindow(h, SW_SHOW); SetForegroundWindow(h);
            wcscpy(g_err, L"Windows Setup blev lukket. Vælg igen, eller genstart.");
            g_screen = SC_CHOOSE; InvalidateRect(h, NULL, FALSE);
        }
        return 0;
    case WM_LOGIN_DONE:
        g_busy = 0;
        if (w == NO_ERROR) { SetWindowTextW(g_ePass, L""); AfterLogin(); }
        else
        {
            if (w == ERROR_LOGON_FAILURE || w == ERROR_ACCESS_DENIED || w == ERROR_INVALID_PASSWORD) wcscpy(g_err, L"Forkert brugernavn eller adgangskode.");
            else if (w == ERROR_BAD_NETPATH || w == ERROR_BAD_NET_NAME || w == ERROR_NO_NET_OR_BAD_PATH)
                swprintf(g_err, 512, L"Kan ikke finde \\\\%s\\%s.", g_server, g_share);
            else swprintf(g_err, 512, L"Kunne ikke logge ind (fejl %u).", (unsigned)w);
            SetFocus(g_ePass); SendMessageW(g_ePass, EM_SETSEL, 0, -1);
        }
        InvalidateRect(h, NULL, FALSE);
        return 0;
    case WM_SETUP_DONE:
        /* setup.exe kan give stafetten videre til SetupHost.exe: vent til ingen Setup-proces kører mere */
        CloseHandle(g_setupProc); g_setupProc = NULL;
        g_setupGone = 0;
        SetTimer(h, 3, 3000, NULL);
        return 0;
    case WM_DESTROY: PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(h, m, w, l);
}

static void LoadFonts(void)
{
    const wchar_t *files[] = { L"X:\\sources\\segoeui.ttf", L"X:\\Windows\\Fonts\\segoeui.ttf", L"X:\\Windows\\Fonts\\seguisb.ttf",
                               L"X:\\Windows\\Fonts\\segoeuisl.ttf", L"X:\\Windows\\Fonts\\consola.ttf", L"X:\\Windows\\Fonts\\seguisym.ttf" };
    for (int i = 0; i < 6; i++) if (FileExists(files[i])) AddFontResourceExW(files[i], FR_PRIVATE, NULL);
}

int WINAPI wWinMain(HINSTANCE inst, HINSTANCE prev, LPWSTR cmd, int show)
{
    (void)prev; (void)cmd; (void)show;
    int argc; wchar_t **argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    BOOL windowed = FALSE;
    for (int i = 1; i < argc; i++)
    {
        if (!_wcsicmp(argv[i], L"/server") && i + 1 < argc) CopyMax(g_server, 64, argv[++i]);
        else if (!_wcsicmp(argv[i], L"/share") && i + 1 < argc) CopyMax(g_share, 64, argv[++i]);
        else if (!_wcsicmp(argv[i], L"/root") && i + 1 < argc) CopyMax(g_root, MAX_PATH, argv[++i]);
        else if (!_wcsicmp(argv[i], L"/name") && i + 1 < argc) CopyMax(g_name, 64, argv[++i]);
        else if (!_wcsicmp(argv[i], L"/test") && i + 1 < argc) { g_test = TRUE; windowed = TRUE; CopyMax(g_root, MAX_PATH, argv[++i]); }
        else if (!_wcsicmp(argv[i], L"/shots") && i + 1 < argc) CopyMax(g_shots, MAX_PATH, argv[++i]);
    }
    swprintf(g_label, 96, L"Installation fra %s", g_name[0] ? g_name : (g_server[0] ? g_server : L"serveren"));
    if (g_test) swprintf(g_outXml, MAX_PATH, L"%s\\unattend.xml", g_root);

    SetProcessDPIAware();
    ULONG_PTR tok; GpStartupInput gsi = { 1, NULL, FALSE, FALSE }; GdiplusStartup(&tok, &gsi, NULL);
    LoadFonts();
    g_W = windowed ? 1280 : GetSystemMetrics(SM_CXSCREEN);
    g_H = windowed ? 800 : GetSystemMetrics(SM_CYSCREEN);
    float sx = g_W / 1280.0f, sy = g_H / 800.0f; g_s = sx < sy ? sx : sy;
    if (g_s < 0.75f) g_s = 0.75f;
    if (g_s > 1.8f) g_s = 1.8f;
    MakeFonts();
    g_fieldBrush = CreateSolidBrush(RGBOF(C_FIELD));

    WNDCLASSW wc = { 0 };
    wc.lpfnWndProc = WndProc; wc.hInstance = inst; wc.lpszClassName = L"WinSetupGui"; wc.hCursor = LoadCursorW(NULL, IDC_ARROW);
    RegisterClassW(&wc);
    g_wnd = CreateWindowExW(windowed ? 0 : WS_EX_TOPMOST, L"WinSetupGui", L"WinSetup", WS_POPUP | WS_CLIPCHILDREN, 0, 0, g_W, g_H, NULL, NULL, inst, NULL);
    g_eUser = MkEdit(0); g_ePass = MkEdit(ES_PASSWORD); g_ePc = MkEdit(ES_UPPERCASE); g_eWin = MkEdit(0);
    SendMessageW(g_ePass, EM_SETPASSWORDCHAR, 0x25CF, 0);
    SendMessageW(g_ePc, EM_SETLIMITTEXT, 15, 0); SendMessageW(g_eWin, EM_SETLIMITTEXT, 20, 0);
    ShowWindow(g_wnd, SW_SHOW); UpdateWindow(g_wnd);
    if (!windowed) SetWindowPos(g_wnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);   /* Setup skal kunne komme foran */

    if (g_shots[0]) { FindNetwork(); Shots(); return 0; }
    for (int i = 1; i < argc; i++)
        if (!_wcsicmp(argv[i], L"/selftest") && i + 5 < argc && g_test)
        {   /* /selftest <pc> <bruger> <mode> <profil|-> <udrulning|->: lav svarfilen og afslut */
            FindNetwork(); LoadData(); ReadMachine(); ReadDisks();
            for (int j = 1; j < argc; j++) if (!_wcsicmp(argv[j], L"/autodisk")) g_autoDisk = TRUE;
            StartInstall(argv[i + 1], argv[i + 2], argv[i + 3], wcscmp(argv[i + 4], L"-") ? argv[i + 4] : L"", wcscmp(argv[i + 5], L"-") ? argv[i + 5] : L"");
            return g_screen == SC_RUNNING ? 0 : 1;
        }
    if (g_test && FindNetwork()) AfterLogin();
    SetTimer(g_wnd, 1, 30, NULL);

    MSG msg;
    while (GetMessageW(&msg, NULL, 0, 0) > 0) { TranslateMessage(&msg); DispatchMessageW(&msg); }
    return 0;
}
