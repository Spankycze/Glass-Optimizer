# atlas: Spankyho Optimizer (1607 LOC, 21 files) | budget 2048 | rendered 1933 tok

## external/DNS-Hop/website/src/data/site.ts (#1 — imported by 2 file(s))
export type Accent = 'sky' | 'mint' | 'sun'
export type StageModeId = 'benchmark' | 'profiles' | 'desktop'
export type GitHubRepo =
export type GitHubAsset =
export type GitHubRelease =
used by: external/DNS-Hop/website/src/App.tsx, external/DNS-Hop/website/src/hooks/useDnsHopSnapshot.ts

## external/DNS-Hop/website/src/hooks/useDnsHopSnapshot.ts (#2 — imported by 1 file(s))
type SnapshotState =
function sumDownloads(releases: GitHubRelease[])
async function loadJson<T>(url: string, signal: AbortSignal): Promise<T>
export function useDnsHopSnapshot()
async function run()
imports: external/DNS-Hop/website/src/data/site.ts
used by: external/DNS-Hop/website/src/App.tsx

## artifacts/backup/code-cleanup-v15.3.31/remove_final_dead_methods.py (#3)
def skip_string(s,i)
def find_end(s,start)

## artifacts/backup/code-cleanup-v15.3.31/remove_last_dead_methods.py (#4)
def skip_string(s,i)
def find_end(s,start)

## artifacts/backup/code-cleanup-v15.3.31/remove_last_four.py (#5)
def skip_string(s,i)
def find_end(s,start)

## artifacts/backup/code-cleanup-v15.3.31/remove_v134_cascade.py (#6)
def skip_string(s,i)
def find_end(s,start)

## native/Spanky.NativeCore/src/lib.rs (#7)
pub struct Guid
    pub data1: u32,
    pub data2: u16,
    pub data3: u16,
    pub data4: [u8; 8],
struct SpDevInfoData
    cbSize: u32,
    classGuid: Guid,
    devInst: u32,
    reserved: usize,
type Handle = *mut c_void
type Bool = i32
const INVALID_HANDLE: Handle = -1isize as Handle
const DIGCF_PRESENT: u32 = 0x2
const DIGCF_ALLCLASSES: u32 = 0x4
const SPDRP_DEVICEDESC: u32 = 0
const SPDRP_CLASS: u32 = 7
const SPDRP_FRIENDLYNAME: u32 = 12
fn clean_field(value: String) -> String
unsafe fn property(set: Handle, data: &mut SpDevInfoData, property: u32) -> String
unsafe fn enumerate_devices() -> String
pub extern "C" fn spanky_native_version() -> u32 { 0x000D0700 }
pub unsafe extern "C" fn spanky_get_active_power_scheme(out_guid: *mut Guid) -> i32
pub unsafe extern "C" fn spanky_set_active_power_scheme(guid: *const Guid) -> i32
struct QueryServiceConfig { service_type:u32, start_type:u32, error_control:u32, binary:*mut u16, group:*mut u16, tag:u32, deps:*mut u16, account:*mut u16, display:*mut u16 }
    struct QueryServiceConfig { service_type:u32, start_type:u32, error_control:u32, binary:*mut u16, group:*mut u16, tag:u32, deps:*mut u16, account:*mut u16, display:*mut u16 }
    struct QueryServiceConfig { service_type:u32, start_type:u32, error_control:u32, binary:*mut u16, group:*mut u16, tag:u32, deps:*mut u16, account:*mut u16, display:*mut u16 }
    struct QueryServiceConfig { service_type:u32, start_type:u32, error_control:u32, binary:*mut u16, group:*mut u16, tag:u32, deps:*mut u16, account:*mut u16, display:*mut u16 }
    struct QueryServiceConfig { service_type:u32, start_type:u32, error_control:u32, binary:*mut u16, group:*mut u16, tag:u32, deps:*mut u16, account:*mut u16, display:*mut u16 }
    struct QueryServiceConfig { service_type:u32, start_type:u32, error_control:u32, binary:*mut u16, group:*mut u16, tag:u32, deps:*mut u16, account:*mut u16, display:*mut u16 }
    struct QueryServiceConfig { service_type:u32, start_type:u32, error_control:u32, binary:*mut u16, group:*mut u16, tag:u32, deps:*mut u16, account:*mut u16, display:*mut u16 }
    struct QueryServiceConfig { service_type:u32, start_type:u32, error_control:u32, binary:*mut u16, group:*mut u16, tag:u32, deps:*mut u16, account:*mut u16, display:*mut u16 }
    struct QueryServiceConfig { service_type:u32, start_type:u32, error_control:u32, binary:*mut u16, group:*mut u16, tag:u32, deps:*mut u16, account:*mut u16, display:*mut u16 }
    struct QueryServiceConfig { service_type:u32, start_type:u32, error_control:u32, binary:*mut u16, group:*mut u16, tag:u32, deps:*mut u16, account:*mut u16, display:*mut u16 }
pub unsafe extern "C" fn spanky_query_service_start_type(name: *const u16, start_type: *mut u32, error: *mut i32) -> i32
pub unsafe extern "C" fn spanky_change_service_start_type(
pub unsafe extern "C" fn spanky_get_present_devices_utf8(buffer: *mut u8, capacity: usize) -> isize

## artifacts/backup/code-cleanup-v15.3.31/remove_dead_methods.py (#8)
def find_end(s,start)

## external/DNS-Hop/website/src/Legal.tsx (#9 — imported by 1 file(s))
function AddressBlock()
function BackLink()
export function ImprintPage()
export function PrivacyPage()
used by: external/DNS-Hop/website/src/App.tsx

## _patch_build_console.mjs (#10)
function addHelper(s)

## external/DNS-Hop/website/src/App.tsx (#13)
function formatCompactNumber(value: number)
function formatBytes(bytes: number)
function assetKind(asset: GitHubAsset)
type Route = 'home' | 'privacy' | 'imprint'
function resolveRoute(): Route
function useHashRoute(): Route
const onHash = () =>
imports: external/DNS-Hop/website/src/Legal.tsx, external/DNS-Hop/website/src/data/site.ts, external/DNS-Hop/website/src/hooks/useDnsHopSnapshot.ts

## make-real-hardware-icons.js (#17)
const wrap=(body)=>`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 96 96"><defs><linearGradient id="m" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#E9EEF6"/><stop offset=".45" stop-color="#687489"/><stop offset="1" stop-color="#232A35"/></linearGradient><linearGradient id="b" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#1E293B"/><stop offset="1" stop-color="#090D14"/></linearGradient></defs>${body}</svg>`

## tools/measure-tts-persistent.py (#19)
async def one(i)
async def main()

## tools/openwakeword/wakeword_bridge.py (#20)
BASE = os.path.dirname(os.path.abspath(__file__))
MODELS = os.path.join(BASE, "models")
CUSTOM = os.path.join(MODELS, "spanky.onnx")
FALLBACK = os.path.join(MODELS, "hey_jarvis_v0.1.onnx")
WAKE_MODEL = CUSTOM if os.path.exists(CUSTOM) else FALLBACK
MELSPEC = os.path.join(MODELS, "melspectrogram.onnx")
EMBED = os.path.join(MODELS, "embedding_model.onnx")
FRAME_BYTES = 2560
THRESHOLD = 0.55
REFRACTORY_SECONDS = 2.0

## tools/rebuild_reference_icons.py (#21)
def svg(name,body,c1="#55d8ff",c2="#315cff")

[6 low-rank file(s) collapsed: ./* (2), external/DNS-Hop/website/* (2), external/DNS-Hop/website/src/* (2)]
