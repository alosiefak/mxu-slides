//! A small C interface to the `automerge` crate, for MxU Slides for Windows.
//!
//! The Mac app keeps every library document as an Automerge document. It reaches Automerge's Rust
//! core through automerge-swift 0.7.2, which builds the `automerge` crate 0.7.2. Windows needs the
//! same core, so this crate pins that exact version (Cargo.toml) and the same dependency versions
//! (Cargo.lock started from automerge-swift's own lock file).
//!
//! The interface is deliberately small and coarse, so the C# side makes few calls and handles
//! few pointers:
//!
//! - a document is an opaque handle (`MxuDoc`), freed with `mxu_am_free`;
//! - bytes coming back (a saved document, a tree, a list of changes) are an `MxuBytes`, freed with
//!   `mxu_bytes_free`;
//! - `mxu_am_read` hands back the whole document as one JSON tree, every value tagged with its
//!   Automerge type, so C# can apply the Mac's decoding rules exactly (an Int is not an F64);
//! - `mxu_am_apply` takes a whole edit as one JSON list of operations, applies it in one
//!   transaction and commits it. If any operation fails, none of them happen.
//!
//! What `mxu_am_apply` can write is limited on purpose to what the Mac's encoder writes for the
//! shared schema: maps, lists, strings, 64-bit integers, doubles and booleans. It cannot make text
//! objects, counters, timestamps, bytes or nulls, so a bug on the C# side can't put a kind of value
//! into a document that the Mac's decoder would refuse.
//!
//! Every function returns a status: 0 for success, 1 when Automerge (or the input) refused, 2 if
//! Rust panicked (caught here, so a panic never unwinds into C#), 3 for a bad argument. The reason
//! is in `mxu_last_error`, per thread.

use std::cell::RefCell;
use std::panic::{catch_unwind, AssertUnwindSafe};

use automerge::transaction::{CommitOptions, Transactable};
use automerge::{ActorId, AutoCommit, ChangeHash, ObjId, ObjType, ReadDoc, ScalarValue, Value, ROOT};
use serde::Deserialize;

/// Bumped whenever a function's meaning changes; the C# side refuses a DLL it doesn't expect.
pub const FFI_VERSION: u32 = 1;

/// The automerge crate this was built against (kept in step with Cargo.toml's `=` pin).
const AUTOMERGE_VERSION: &str = "0.7.2";

const OK: i32 = 0;
const FAILED: i32 = 1;
const PANICKED: i32 = 2;
const BAD_ARGUMENT: i32 = 3;

/// The deepest a document may nest before reading it is refused (the shared schema nests about ten
/// levels; this only stops a damaged file from exhausting the stack).
const MAX_DEPTH: usize = 256;

/// One Automerge document, owned by the C# side through a safe handle.
pub struct MxuDoc {
    doc: AutoCommit,
}

/// Bytes handed to the C# side. Free them with `mxu_bytes_free`.
#[repr(C)]
pub struct MxuBytes {
    pub ptr: *mut u8,
    pub len: usize,
}

thread_local! {
    static LAST_ERROR: RefCell<String> = const { RefCell::new(String::new()) };
}

fn set_error(message: String) {
    LAST_ERROR.with(|e| *e.borrow_mut() = message);
}

/// Runs `body`, turning an error into status 1 and a panic into status 2, with the reason saved.
fn guard<F: FnOnce() -> Result<(), String>>(body: F) -> i32 {
    match catch_unwind(AssertUnwindSafe(body)) {
        Ok(Ok(())) => OK,
        Ok(Err(message)) => {
            set_error(message);
            FAILED
        }
        Err(panic) => {
            let reason = panic
                .downcast_ref::<&str>()
                .map(|s| s.to_string())
                .or_else(|| panic.downcast_ref::<String>().cloned())
                .unwrap_or_else(|| "unknown panic".to_string());
            set_error(format!("automerge panicked: {reason}"));
            PANICKED
        }
    }
}

fn give(bytes: Vec<u8>, out: *mut MxuBytes) {
    let boxed = bytes.into_boxed_slice();
    let len = boxed.len();
    let ptr = Box::into_raw(boxed) as *mut u8;
    // SAFETY: the caller checked `out` is not null.
    unsafe { *out = MxuBytes { ptr, len } };
}

/// Borrows `len` bytes at `ptr` (an empty slice for a null pointer with no length).
fn input<'a>(ptr: *const u8, len: usize) -> Result<&'a [u8], String> {
    if len == 0 {
        Ok(&[])
    } else if ptr.is_null() {
        Err("null input with a length".to_string())
    } else {
        // SAFETY: the C# side passes a pinned buffer of exactly `len` bytes for the call's duration.
        Ok(unsafe { std::slice::from_raw_parts(ptr, len) })
    }
}

fn document<'a>(doc: *mut MxuDoc) -> Option<&'a mut MxuDoc> {
    // SAFETY: a non-null handle is one `mxu_am_new`/`mxu_am_load`/`mxu_am_fork*` gave out and the
    // C# side hasn't freed; its SafeHandle makes sure only one call uses it at a time.
    unsafe { doc.as_mut() }
}

fn hand_out(doc: AutoCommit, out: *mut *mut MxuDoc) {
    let boxed = Box::new(MxuDoc { doc });
    // SAFETY: the caller checked `out` is not null.
    unsafe { *out = Box::into_raw(boxed) };
}

fn heads_from(bytes: &[u8]) -> Result<Vec<ChangeHash>, String> {
    if bytes.len() % 32 != 0 {
        return Err("heads must be whole 32-byte hashes".to_string());
    }
    bytes
        .chunks(32)
        .map(|chunk| ChangeHash::try_from(chunk).map_err(|e| e.to_string()))
        .collect()
}

// MARK: Version and errors

/// The interface version (`FFI_VERSION`); the C# side checks it before anything else.
#[no_mangle]
pub extern "C" fn mxu_ffi_version() -> u32 {
    FFI_VERSION
}

/// A description such as "mxu-automerge-ffi 1 (automerge 0.7.2)", as UTF-8.
#[no_mangle]
pub extern "C" fn mxu_version(out: *mut MxuBytes) -> i32 {
    if out.is_null() {
        return BAD_ARGUMENT;
    }
    guard(|| {
        give(format!("mxu-automerge-ffi {FFI_VERSION} (automerge {AUTOMERGE_VERSION})").into_bytes(), out);
        Ok(())
    })
}

/// The reason the last call on this thread failed, as UTF-8.
#[no_mangle]
pub extern "C" fn mxu_last_error(out: *mut MxuBytes) -> i32 {
    if out.is_null() {
        return BAD_ARGUMENT;
    }
    let message = LAST_ERROR.with(|e| e.borrow().clone());
    give(message.into_bytes(), out);
    OK
}

/// Frees bytes this library handed out. Safe to call with a null pointer.
#[no_mangle]
pub extern "C" fn mxu_bytes_free(bytes: MxuBytes) {
    if !bytes.ptr.is_null() {
        // SAFETY: `ptr`/`len` came from `give`, which leaked a boxed slice of exactly that length.
        unsafe { drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(bytes.ptr, bytes.len))) };
    }
}

// MARK: Making, loading, saving and freeing

/// A new, empty document with a random actor.
#[no_mangle]
pub extern "C" fn mxu_am_new(out: *mut *mut MxuDoc) -> i32 {
    if out.is_null() {
        return BAD_ARGUMENT;
    }
    guard(|| {
        hand_out(AutoCommit::new(), out);
        Ok(())
    })
}

/// Loads a saved document (a whole `.automerge` file). Automerge checks it as it loads; a damaged
/// or unknown file is refused rather than half-read.
#[no_mangle]
pub extern "C" fn mxu_am_load(data: *const u8, len: usize, out: *mut *mut MxuDoc) -> i32 {
    if out.is_null() {
        return BAD_ARGUMENT;
    }
    guard(|| {
        let bytes = input(data, len)?;
        let doc = AutoCommit::load(bytes).map_err(|e| format!("not a readable Automerge document: {e}"))?;
        hand_out(doc, out);
        Ok(())
    })
}

/// Frees a document. Safe to call with a null pointer.
#[no_mangle]
pub extern "C" fn mxu_am_free(doc: *mut MxuDoc) {
    if !doc.is_null() {
        // SAFETY: the handle came from `hand_out` and the C# SafeHandle frees it exactly once.
        let _ = catch_unwind(AssertUnwindSafe(|| unsafe { drop(Box::from_raw(doc)) }));
    }
}

/// Saves the whole document, compressed, exactly as the Mac's `Document.save()` does.
#[no_mangle]
pub extern "C" fn mxu_am_save(doc: *mut MxuDoc, out: *mut MxuBytes) -> i32 {
    let Some(doc) = document(doc) else { return BAD_ARGUMENT };
    if out.is_null() {
        return BAD_ARGUMENT;
    }
    guard(|| {
        give(doc.doc.save(), out);
        Ok(())
    })
}

/// A copy of the document, with a new random actor.
#[no_mangle]
pub extern "C" fn mxu_am_fork(doc: *mut MxuDoc, out: *mut *mut MxuDoc) -> i32 {
    let Some(doc) = document(doc) else { return BAD_ARGUMENT };
    if out.is_null() {
        return BAD_ARGUMENT;
    }
    guard(|| {
        hand_out(doc.doc.fork(), out);
        Ok(())
    })
}

/// A copy of the document as it was at `heads` (concatenated 32-byte change hashes), with a new
/// random actor. An edit made on it is concurrent with everything after those heads, so merging
/// it back combines both, as two Macs' edits do.
#[no_mangle]
pub extern "C" fn mxu_am_fork_at(doc: *mut MxuDoc, heads: *const u8, heads_len: usize, out: *mut *mut MxuDoc) -> i32 {
    let Some(doc) = document(doc) else { return BAD_ARGUMENT };
    if out.is_null() {
        return BAD_ARGUMENT;
    }
    guard(|| {
        let heads = heads_from(input(heads, heads_len)?)?;
        let forked = doc.doc.fork_at(&heads).map_err(|e| format!("can't go back to those heads: {e}"))?;
        hand_out(forked, out);
        Ok(())
    })
}

/// Merges every change `other` has that `doc` lacks into `doc`. `other` is left as it was.
#[no_mangle]
pub extern "C" fn mxu_am_merge(doc: *mut MxuDoc, other: *mut MxuDoc) -> i32 {
    if doc == other {
        return OK;
    }
    let Some(doc) = document(doc) else { return BAD_ARGUMENT };
    let Some(other) = document(other) else { return BAD_ARGUMENT };
    guard(|| {
        doc.doc.merge(&mut other.doc).map_err(|e| format!("merge refused: {e}"))?;
        Ok(())
    })
}

// MARK: History

/// The document's heads: its newest changes, as concatenated 32-byte hashes, sorted.
#[no_mangle]
pub extern "C" fn mxu_am_heads(doc: *mut MxuDoc, out: *mut MxuBytes) -> i32 {
    let Some(doc) = document(doc) else { return BAD_ARGUMENT };
    if out.is_null() {
        return BAD_ARGUMENT;
    }
    guard(|| {
        let mut heads = doc.doc.get_heads();
        heads.sort();
        give(heads.iter().flat_map(|h| h.0).collect(), out);
        Ok(())
    })
}

/// Every change in the document, oldest first, as a JSON list:
/// `[{"hash":"…","actor":"…","seq":1,"time":0,"message":null,"deps":["…"]}]` (hex strings).
#[no_mangle]
pub extern "C" fn mxu_am_changes(doc: *mut MxuDoc, out: *mut MxuBytes) -> i32 {
    let Some(doc) = document(doc) else { return BAD_ARGUMENT };
    if out.is_null() {
        return BAD_ARGUMENT;
    }
    guard(|| {
        let changes: Vec<serde_json::Value> = doc
            .doc
            .get_changes(&[])
            .iter()
            .map(|change| {
                serde_json::json!({
                    "hash": hex::encode(change.hash().0),
                    "actor": change.actor_id().to_hex_string(),
                    "seq": change.seq(),
                    "time": change.timestamp(),
                    "message": change.message(),
                    "deps": change.deps().iter().map(|d| hex::encode(d.0)).collect::<Vec<_>>(),
                })
            })
            .collect();
        give(serde_json::to_vec(&changes).map_err(|e| e.to_string())?, out);
        Ok(())
    })
}

/// The actor new changes are made under, as raw bytes.
#[no_mangle]
pub extern "C" fn mxu_am_get_actor(doc: *mut MxuDoc, out: *mut MxuBytes) -> i32 {
    let Some(doc) = document(doc) else { return BAD_ARGUMENT };
    if out.is_null() {
        return BAD_ARGUMENT;
    }
    guard(|| {
        give(doc.doc.get_actor().to_bytes().to_vec(), out);
        Ok(())
    })
}

/// Sets the actor new changes are made under (raw bytes, at least one).
#[no_mangle]
pub extern "C" fn mxu_am_set_actor(doc: *mut MxuDoc, actor: *const u8, len: usize) -> i32 {
    let Some(doc) = document(doc) else { return BAD_ARGUMENT };
    guard(|| {
        let bytes = input(actor, len)?;
        if bytes.is_empty() {
            return Err("an actor needs at least one byte".to_string());
        }
        doc.doc.set_actor(ActorId::from(bytes));
        Ok(())
    })
}

// MARK: Reading

/// The whole document as one JSON tree, with every value tagged by its Automerge type:
///
/// - a map is `{"m":{key: value, …}}` (a legacy table `{"tb":{…}}`), a list `{"l":[…]}`, and a
///   text object `{"t":"…"}`;
/// - a string is `{"s":"…"}`, a signed integer `{"i":…}`, an unsigned one `{"u":…}`, a double
///   `{"f":…}` (or `{"fx":"NaN"}`, `"Infinity"`, `"-Infinity"`), a boolean `{"b":…}`, null
///   `{"n":null}`, a counter `{"c":…}`, a timestamp `{"ts":…}`, bytes `{"by":"hex"}`, and a value
///   from a newer Automerge `{"x":type code,"by":"hex"}`.
///
/// Where two machines set the same key at once, the value shown is Automerge's winner, the same
/// one the Mac's decoder reads.
#[no_mangle]
pub extern "C" fn mxu_am_read(doc: *mut MxuDoc, out: *mut MxuBytes) -> i32 {
    let Some(doc) = document(doc) else { return BAD_ARGUMENT };
    if out.is_null() {
        return BAD_ARGUMENT;
    }
    guard(|| {
        let mut json = String::with_capacity(4096);
        write_object(&doc.doc, &ROOT, ObjType::Map, &mut json, 0)?;
        give(json.into_bytes(), out);
        Ok(())
    })
}

fn write_object(doc: &AutoCommit, obj: &ObjId, ty: ObjType, out: &mut String, depth: usize) -> Result<(), String> {
    if depth > MAX_DEPTH {
        return Err("the document nests too deeply to read".to_string());
    }
    match ty {
        ObjType::Map | ObjType::Table => {
            out.push_str(if ty == ObjType::Map { "{\"m\":{" } else { "{\"tb\":{" });
            let mut first = true;
            for key in doc.keys(obj) {
                if let Some((value, id)) = doc.get(obj, key.as_str()).map_err(|e| e.to_string())? {
                    if !first {
                        out.push(',');
                    }
                    first = false;
                    write_string(&key, out);
                    out.push(':');
                    write_value(doc, value, &id, out, depth)?;
                }
            }
            out.push_str("}}");
        }
        ObjType::List => {
            out.push_str("{\"l\":[");
            for index in 0..doc.length(obj) {
                if index > 0 {
                    out.push(',');
                }
                match doc.get(obj, index).map_err(|e| e.to_string())? {
                    Some((value, id)) => write_value(doc, value, &id, out, depth)?,
                    None => return Err(format!("list element {index} vanished while reading")),
                }
            }
            out.push_str("]}");
        }
        ObjType::Text => {
            out.push_str("{\"t\":");
            write_string(&doc.text(obj).map_err(|e| e.to_string())?, out);
            out.push('}');
        }
    }
    Ok(())
}

fn write_value(doc: &AutoCommit, value: Value<'_>, id: &ObjId, out: &mut String, depth: usize) -> Result<(), String> {
    match value {
        Value::Object(ty) => write_object(doc, id, ty, out, depth + 1),
        Value::Scalar(scalar) => {
            write_scalar(&scalar, out);
            Ok(())
        }
    }
}

fn write_scalar(value: &ScalarValue, out: &mut String) {
    match value {
        ScalarValue::Str(s) => {
            out.push_str("{\"s\":");
            write_string(s, out);
            out.push('}');
        }
        ScalarValue::Int(i) => out.push_str(&format!("{{\"i\":{i}}}")),
        ScalarValue::Uint(u) => out.push_str(&format!("{{\"u\":{u}}}")),
        ScalarValue::F64(f) if f.is_finite() => {
            // serde_json writes the shortest text that reads back as exactly this double.
            out.push_str("{\"f\":");
            out.push_str(&serde_json::to_string(f).unwrap_or_else(|_| "0".to_string()));
            out.push('}');
        }
        ScalarValue::F64(f) => {
            let name = if f.is_nan() { "NaN" } else if *f > 0.0 { "Infinity" } else { "-Infinity" };
            out.push_str(&format!("{{\"fx\":\"{name}\"}}"));
        }
        ScalarValue::Counter(c) => out.push_str(&format!("{{\"c\":{}}}", i64::from(c))),
        ScalarValue::Timestamp(t) => out.push_str(&format!("{{\"ts\":{t}}}")),
        ScalarValue::Boolean(b) => out.push_str(if *b { "{\"b\":true}" } else { "{\"b\":false}" }),
        ScalarValue::Null => out.push_str("{\"n\":null}"),
        ScalarValue::Bytes(bytes) => out.push_str(&format!("{{\"by\":\"{}\"}}", hex::encode(bytes))),
        ScalarValue::Unknown { type_code, bytes } => {
            out.push_str(&format!("{{\"x\":{type_code},\"by\":\"{}\"}}", hex::encode(bytes)))
        }
    }
}

fn write_string(s: &str, out: &mut String) {
    out.push_str(&serde_json::to_string(s).unwrap_or_else(|_| "\"\"".to_string()));
}

// MARK: Writing

/// Where an operation acts: a map key or a list index.
#[derive(Deserialize)]
#[serde(untagged)]
enum Place {
    Index(usize),
    Key(String),
}

/// The only kinds of value an edit may write: what the Mac's encoder writes for the shared schema.
#[derive(Deserialize)]
enum Scalar {
    #[serde(rename = "s")]
    Str(String),
    #[serde(rename = "i")]
    Int(i64),
    #[serde(rename = "f")]
    F64(f64),
    #[serde(rename = "b")]
    Bool(bool),
}

/// The only kinds of object an edit may make.
#[derive(Deserialize, Clone, Copy)]
enum Kind {
    #[serde(rename = "map")]
    Map,
    #[serde(rename = "list")]
    List,
}

/// One operation of an edit. `p` is the path from the document's root to the object it acts on.
#[derive(Deserialize)]
#[serde(tag = "o")]
enum Op {
    /// Sets a map key or replaces a list element with a value.
    #[serde(rename = "put")]
    Put { p: Vec<Place>, k: Place, v: Scalar },
    /// Sets a map key or replaces a list element with a new, empty map or list.
    #[serde(rename = "putObj")]
    PutObj { p: Vec<Place>, k: Place, t: Kind },
    /// Inserts a value into a list before index `i` (`i` = the length appends).
    #[serde(rename = "ins")]
    Insert { p: Vec<Place>, i: usize, v: Scalar },
    /// Inserts a new, empty map or list into a list before index `i`.
    #[serde(rename = "insObj")]
    InsertObj { p: Vec<Place>, i: usize, t: Kind },
    /// Removes a map key or a list element.
    #[serde(rename = "del")]
    Delete { p: Vec<Place>, k: Place },
}

impl Scalar {
    fn value(self) -> Result<ScalarValue, String> {
        Ok(match self {
            Scalar::Str(s) => ScalarValue::Str(s.into()),
            Scalar::Int(i) => ScalarValue::Int(i),
            Scalar::F64(f) if f.is_finite() => ScalarValue::F64(f),
            // The Mac's encoder refuses these too ("Unable to encode Double.nan").
            Scalar::F64(_) => return Err("a double that isn't a finite number".to_string()),
            Scalar::Bool(b) => ScalarValue::Boolean(b),
        })
    }
}

impl Kind {
    fn obj_type(self) -> ObjType {
        match self {
            Kind::Map => ObjType::Map,
            Kind::List => ObjType::List,
        }
    }
}

/// Follows `path` from the root to an object. Every step must land on a map or a list.
fn resolve(doc: &AutoCommit, path: &[Place]) -> Result<(ObjId, ObjType), String> {
    let mut obj = ROOT;
    let mut ty = ObjType::Map;
    for (step, place) in path.iter().enumerate() {
        let found = match place {
            Place::Key(key) if is_map(ty) => doc.get(&obj, key.as_str()),
            Place::Index(index) if ty == ObjType::List => doc.get(&obj, *index),
            _ => return Err(format!("path step {step} doesn't fit a {ty:?}")),
        }
        .map_err(|e| e.to_string())?;
        match found {
            Some((Value::Object(next), id)) => {
                obj = id;
                ty = next;
            }
            _ => return Err(format!("path step {step} isn't an object")),
        }
    }
    Ok((obj, ty))
}

fn is_map(ty: ObjType) -> bool {
    matches!(ty, ObjType::Map | ObjType::Table)
}

fn apply_one(doc: &mut AutoCommit, op: Op) -> Result<(), String> {
    match op {
        Op::Put { p, k, v } => {
            let (obj, ty) = resolve(doc, &p)?;
            let value = v.value()?;
            match k {
                Place::Key(key) if is_map(ty) => doc.put(&obj, key, value),
                Place::Index(index) if ty == ObjType::List => doc.put(&obj, index, value),
                _ => return Err("put: the key doesn't fit the object".to_string()),
            }
            .map_err(|e| e.to_string())
        }
        Op::PutObj { p, k, t } => {
            let (obj, ty) = resolve(doc, &p)?;
            match k {
                Place::Key(key) if is_map(ty) => doc.put_object(&obj, key, t.obj_type()),
                Place::Index(index) if ty == ObjType::List => doc.put_object(&obj, index, t.obj_type()),
                _ => return Err("putObj: the key doesn't fit the object".to_string()),
            }
            .map(|_| ())
            .map_err(|e| e.to_string())
        }
        Op::Insert { p, i, v } => {
            let (obj, ty) = resolve(doc, &p)?;
            if ty != ObjType::List {
                return Err("ins: not a list".to_string());
            }
            doc.insert(&obj, i, v.value()?).map_err(|e| e.to_string())
        }
        Op::InsertObj { p, i, t } => {
            let (obj, ty) = resolve(doc, &p)?;
            if ty != ObjType::List {
                return Err("insObj: not a list".to_string());
            }
            doc.insert_object(&obj, i, t.obj_type()).map(|_| ()).map_err(|e| e.to_string())
        }
        Op::Delete { p, k } => {
            let (obj, ty) = resolve(doc, &p)?;
            match k {
                Place::Key(key) if is_map(ty) => doc.delete(&obj, key),
                Place::Index(index) if ty == ObjType::List => doc.delete(&obj, index),
                _ => return Err("del: the key doesn't fit the object".to_string()),
            }
            .map_err(|e| e.to_string())
        }
    }
}

/// Applies an edit (a JSON list of operations, see `Op`) in one transaction and commits it with
/// `message` (UTF-8; ignored unless `has_message` is non-zero) and `time` (seconds since 1970, as
/// the Mac's `commitWith` records). If any operation fails, the whole edit is rolled back and the
/// document is as it was. `out_count` gets the number of operations applied; an empty edit commits
/// nothing.
#[no_mangle]
pub extern "C" fn mxu_am_apply(
    doc: *mut MxuDoc,
    ops: *const u8,
    ops_len: usize,
    message: *const u8,
    message_len: usize,
    has_message: i32,
    time: i64,
    out_count: *mut u64,
) -> i32 {
    let Some(doc) = document(doc) else { return BAD_ARGUMENT };
    if out_count.is_null() {
        return BAD_ARGUMENT;
    }
    // SAFETY: checked not null above.
    unsafe { *out_count = 0 };
    let status = guard(|| {
        let ops: Vec<Op> = serde_json::from_slice(input(ops, ops_len)?).map_err(|e| format!("edit isn't valid: {e}"))?;
        let message = if has_message != 0 {
            Some(String::from_utf8(input(message, message_len)?.to_vec()).map_err(|e| e.to_string())?)
        } else {
            None
        };
        // Anything left open from before belongs to nobody: start this edit clean.
        doc.doc.rollback();
        let count = ops.len();
        for (index, op) in ops.into_iter().enumerate() {
            if let Err(reason) = apply_one(&mut doc.doc, op) {
                doc.doc.rollback();
                return Err(format!("operation {index}: {reason}"));
            }
        }
        if count > 0 {
            let mut options = CommitOptions::default().with_time(time);
            if let Some(message) = message {
                options = options.with_message(message);
            }
            doc.doc.commit_with(options);
        }
        // SAFETY: checked not null above.
        unsafe { *out_count = count as u64 };
        Ok(())
    });
    if status == PANICKED {
        // A panic mid-edit may have left operations pending; never let them be committed later.
        let _ = catch_unwind(AssertUnwindSafe(|| doc.doc.rollback()));
    }
    status
}
