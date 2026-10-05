# atlas: Spankyho Optimizer (413 LOC, 8 files) | budget 2048 | rendered 1381 tok

## native/Spanky.NativeCore/src/lib.rs (#1)
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

## _patch_build_console.mjs (#2)
function addHelper(s)

## make-real-hardware-icons.js (#4)
const wrap=(body)=>`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 96 96"><defs><linearGradient id="m" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#E9EEF6"/><stop offset=".45" stop-color="#687489"/><stop offset="1" stop-color="#232A35"/></linearGradient><linearGradient id="b" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#1E293B"/><stop offset="1" stop-color="#090D14"/></linearGradient></defs>${body}</svg>`

## tools/measure-tts-persistent.py (#6)
async def one(i)
async def main()

## tools/openwakeword/wakeword_bridge.py (#7)
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

## tools/rebuild_reference_icons.py (#8)
def svg(name,body,c1="#55d8ff",c2="#315cff")

[2 low-rank file(s) collapsed: ./* (2)]
