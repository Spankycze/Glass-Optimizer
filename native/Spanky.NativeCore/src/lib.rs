#![allow(non_snake_case)]

use std::ffi::c_void;
use std::ptr::{null, null_mut};

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Guid {
    pub data1: u32,
    pub data2: u16,
    pub data3: u16,
    pub data4: [u8; 8],
}

#[repr(C)]
struct SpDevInfoData {
    cbSize: u32,
    classGuid: Guid,
    devInst: u32,
    reserved: usize,
}

type Handle = *mut c_void;
type Bool = i32;
const INVALID_HANDLE: Handle = -1isize as Handle;
const DIGCF_PRESENT: u32 = 0x2;
const DIGCF_ALLCLASSES: u32 = 0x4;
const SPDRP_DEVICEDESC: u32 = 0;
const SPDRP_CLASS: u32 = 7;
const SPDRP_FRIENDLYNAME: u32 = 12;

#[link(name = "PowrProf")]
extern "system" {
    fn PowerGetActiveScheme(root: Handle, guid: *mut *mut Guid) -> u32;
    fn PowerSetActiveScheme(root: Handle, guid: *const Guid) -> u32;
}

#[link(name = "Kernel32")]
extern "system" {
    fn LocalFree(mem: Handle) -> Handle;
}

#[link(name = "Advapi32")]
extern "system" {
    fn OpenSCManagerW(machine: *const u16, database: *const u16, access: u32) -> Handle;
    fn OpenServiceW(manager: Handle, name: *const u16, access: u32) -> Handle;
    fn QueryServiceConfigW(service: Handle, config: *mut u8, size: u32, needed: *mut u32) -> Bool;
    fn ChangeServiceConfigW(
        service: Handle,
        service_type: u32,
        start_type: u32,
        error_control: u32,
        binary_path: *const u16,
        load_group: *const u16,
        tag: *mut u32,
        dependencies: *const u16,
        account: *const u16,
        password: *const u16,
        display_name: *const u16,
    ) -> Bool;
    fn CloseServiceHandle(handle: Handle) -> Bool;
}

#[link(name = "Kernel32")]
extern "system" {
    fn GetLastError() -> u32;
}

#[link(name = "Setupapi")]
extern "system" {
    fn SetupDiGetClassDevsW(
        class_guid: *const Guid,
        enumerator: *const u16,
        parent: Handle,
        flags: u32,
    ) -> Handle;
    fn SetupDiEnumDeviceInfo(set: Handle, index: u32, data: *mut SpDevInfoData) -> Bool;
    fn SetupDiGetDeviceRegistryPropertyW(
        set: Handle,
        data: *mut SpDevInfoData,
        property: u32,
        prop_type: *mut u32,
        buffer: *mut u8,
        buffer_size: u32,
        required: *mut u32,
    ) -> Bool;
    fn SetupDiGetDeviceInstanceIdW(
        set: Handle,
        data: *mut SpDevInfoData,
        instance: *mut u16,
        size: u32,
        required: *mut u32,
    ) -> Bool;
    fn SetupDiDestroyDeviceInfoList(set: Handle) -> Bool;
}

fn clean_field(value: String) -> String {
    value.replace(['\t', '\r', '\n'], " ")
}

unsafe fn property(set: Handle, data: &mut SpDevInfoData, property: u32) -> String {
    // SetupAPI returns UTF-16; use aligned storage for these strings.
    let mut buffer = vec![0u16; 2048];
    let mut required = 0u32;
    let mut prop_type = 0u32;
    if SetupDiGetDeviceRegistryPropertyW(
        set,
        data,
        property,
        &mut prop_type,
        buffer.as_mut_ptr().cast::<u8>(),
        (buffer.len() * std::mem::size_of::<u16>()) as u32,
        &mut required,
    ) == 0
    {
        return String::new();
    }
    let units = (required as usize / 2).min(buffer.len());
    let slice = &buffer[..units];
    let end = slice.iter().position(|&x| x == 0).unwrap_or(slice.len());
    clean_field(String::from_utf16_lossy(&slice[..end]))
}

unsafe fn enumerate_devices() -> String {
    let set = SetupDiGetClassDevsW(null(), null(), null_mut(), DIGCF_PRESENT | DIGCF_ALLCLASSES);
    if set == INVALID_HANDLE {
        return String::new();
    }
    let mut output = String::with_capacity(32 * 1024);
    let mut index = 0u32;
    loop {
        let mut data = SpDevInfoData {
            cbSize: std::mem::size_of::<SpDevInfoData>() as u32,
            classGuid: Guid {
                data1: 0,
                data2: 0,
                data3: 0,
                data4: [0; 8],
            },
            devInst: 0,
            reserved: 0,
        };
        if SetupDiEnumDeviceInfo(set, index, &mut data) == 0 {
            break;
        }
        let mut id = vec![0u16; 2048];
        let mut required = 0u32;
        if SetupDiGetDeviceInstanceIdW(
            set,
            &mut data,
            id.as_mut_ptr(),
            id.len() as u32,
            &mut required,
        ) != 0
        {
            let end = id.iter().position(|&x| x == 0).unwrap_or(id.len());
            let instance = clean_field(String::from_utf16_lossy(&id[..end]));
            let class_name = property(set, &mut data, SPDRP_CLASS);
            let mut friendly = property(set, &mut data, SPDRP_FRIENDLYNAME);
            if friendly.is_empty() {
                friendly = property(set, &mut data, SPDRP_DEVICEDESC);
            }
            output.push_str(&instance);
            output.push('\t');
            output.push_str(&class_name);
            output.push('\t');
            output.push_str(&friendly);
            output.push('\n');
        }
        index += 1;
    }
    SetupDiDestroyDeviceInfoList(set);
    output
}

#[no_mangle]
pub extern "C" fn spanky_native_version() -> u32 {
    0x000D0700
}

/// Reads the current Windows power-scheme GUID into the caller-provided output.
///
/// # Safety
/// `out_guid` must point to a writable, properly aligned `Guid` for the duration
/// of this call. It may be null, in which case the function returns error 87.
#[no_mangle]
pub unsafe extern "C" fn spanky_get_active_power_scheme(out_guid: *mut Guid) -> i32 {
    if out_guid.is_null() {
        return 87;
    }
    let mut ptr: *mut Guid = null_mut();
    let result = PowerGetActiveScheme(null_mut(), &mut ptr);
    if result != 0 || ptr.is_null() {
        return result as i32;
    }
    *out_guid = *ptr;
    LocalFree(ptr as Handle);
    0
}

/// Requests activation of the supplied Windows power scheme.
///
/// # Safety
/// `guid` must point to a readable, properly aligned, initialized `Guid`
/// for the duration of the call. Null is rejected with error 87.
#[no_mangle]
pub unsafe extern "C" fn spanky_set_active_power_scheme(guid: *const Guid) -> i32 {
    if guid.is_null() {
        return 87;
    }
    PowerSetActiveScheme(null_mut(), guid) as i32
}

#[repr(C)]
struct QueryServiceConfig {
    service_type: u32,
    start_type: u32,
    error_control: u32,
    binary: *mut u16,
    group: *mut u16,
    tag: u32,
    deps: *mut u16,
    account: *mut u16,
    display: *mut u16,
}

/// Queries the Windows Service Control Manager for a service start type.
///
/// # Safety
/// `name` must be a valid NUL-terminated UTF-16 service name; `start_type`
/// must point to writable aligned `u32` storage. If non-null, `error`
/// must point to writable aligned `i32` storage. The pointers must remain
/// valid throughout the call.
#[no_mangle]
pub unsafe extern "C" fn spanky_query_service_start_type(
    name: *const u16,
    start_type: *mut u32,
    error: *mut i32,
) -> i32 {
    if name.is_null() || start_type.is_null() {
        if !error.is_null() {
            *error = 87;
        }
        return 0;
    }
    let manager = OpenSCManagerW(null(), null(), 1);
    if manager.is_null() {
        if !error.is_null() {
            *error = GetLastError() as i32;
        }
        return 0;
    }
    let service = OpenServiceW(manager, name, 1);
    if service.is_null() {
        if !error.is_null() {
            *error = GetLastError() as i32;
        }
        CloseServiceHandle(manager);
        return 0;
    }
    let mut needed = 0u32;
    QueryServiceConfigW(service, null_mut(), 0, &mut needed);
    // QUERY_SERVICE_CONFIGW begins with two DWORD fields. Reserve aligned
    // DWORD storage so the Windows API can safely write pointers in the
    // structure, and validate its minimum returned size before reading.
    let min_size = std::mem::size_of::<QueryServiceConfig>();
    let mut storage = vec![
        0usize;
        (needed as usize)
            .max(min_size)
            .div_ceil(std::mem::size_of::<usize>())
    ];
    let capacity = storage.len() * std::mem::size_of::<usize>();
    let ok = if capacity <= u32::MAX as usize {
        QueryServiceConfigW(
            service,
            storage.as_mut_ptr().cast::<u8>(),
            capacity as u32,
            &mut needed,
        )
    } else {
        0
    };
    // The API may leave needed unchanged on success; storage is already sized
    // to hold the entire fixed structure, so success is the validity check.
    let valid = ok != 0;
    if valid {
        *start_type = (*(storage.as_ptr() as *const QueryServiceConfig)).start_type;
        if !error.is_null() {
            *error = 0;
        }
    } else if !error.is_null() {
        *error = if ok == 0 { GetLastError() as i32 } else { 13 };
    }
    CloseServiceHandle(service);
    CloseServiceHandle(manager);
    if valid {
        1
    } else {
        0
    }
}

/// Requests a service start-type change through the Windows SCM.
///
/// # Safety
/// `name` must be a valid NUL-terminated UTF-16 service name. If non-null,
/// `error` must point to writable aligned `i32` storage for the call.
#[no_mangle]
pub unsafe extern "C" fn spanky_change_service_start_type(
    name: *const u16,
    start_type: u32,
    error: *mut i32,
) -> i32 {
    if name.is_null() {
        if !error.is_null() {
            *error = 87;
        }
        return 0;
    }
    let manager = OpenSCManagerW(null(), null(), 1);
    if manager.is_null() {
        if !error.is_null() {
            *error = GetLastError() as i32;
        }
        return 0;
    }
    let service = OpenServiceW(manager, name, 2);
    if service.is_null() {
        if !error.is_null() {
            *error = GetLastError() as i32;
        }
        CloseServiceHandle(manager);
        return 0;
    }
    let ok = ChangeServiceConfigW(
        service,
        u32::MAX,
        start_type,
        u32::MAX,
        null(),
        null(),
        null_mut(),
        null(),
        null(),
        null(),
        null(),
    );
    if ok == 0 && !error.is_null() {
        *error = GetLastError() as i32;
    }
    CloseServiceHandle(service);
    CloseServiceHandle(manager);
    if ok != 0 {
        1
    } else {
        0
    }
}

/// Writes a NUL-terminated UTF-8 device list, or returns the required size.
///
/// # Safety
/// When `buffer` is non-null and `capacity` is nonzero, it must point to
/// at least `capacity` writable bytes. The memory must remain valid for the
/// duration of the call. Null or zero capacity is treated as a size query.
#[no_mangle]
pub unsafe extern "C" fn spanky_get_present_devices_utf8(
    buffer: *mut u8,
    capacity: usize,
) -> isize {
    let text = enumerate_devices();
    let bytes = text.as_bytes();
    let required = bytes.len() + 1;
    if buffer.is_null() || capacity == 0 {
        return required as isize;
    }
    if capacity < required {
        return -(required as isize);
    }
    std::ptr::copy_nonoverlapping(bytes.as_ptr(), buffer, bytes.len());
    *buffer.add(bytes.len()) = 0;
    bytes.len() as isize
}
