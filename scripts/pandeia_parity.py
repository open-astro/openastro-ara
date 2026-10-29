"""#1140 Pandeia parity — regenerates the cases client/openastroara_client/test/util/
integration_budget_pandeia_parity_test.dart pins.

Needs STScI's Pandeia engine and its data (not used by CI; the test carries the numbers):
    pip install pandeia.engine==2026.7          # Python >= 3.11 (numpy 2)
    # data (~20 MB) + PSFs (~4 GB): https://stsci.box.com/v/pandeia-data-v2026p7-jwst,
    #   https://stsci.box.com/v/pandeia-psfs-v2026p7-jwst ; synphot 1: https://archive.stsci.edu/hlsps/
    #   reference-atlases/hlsp_reference-atlases_hst_multi_everything_multi_v16_sed.tar
    export pandeia_refdata=.../pandeia_data-2026.7-jwst PSF_DIR=.../pandeia_psfs-2026.7rc1-jwst
    export PYSYN_CDBS=.../grp/redcat/trds
    python scripts/pandeia_parity.py

The cases: JWST NIRCam F200W imaging, Pandeia 2026.7,
detector noise off (readnoise, excess readnoise, dark, flat field, cosmic rays), RAPID readout with 2
groups (a CDS slope: no up-the-ramp shot-noise factor), no background annulus, 1.2" aperture (edge
weighting < 1%). Flat-fnu source normalized at 2.0 um."""
import json, math
from pandeia.engine.calc_utils import build_default_calc
from pandeia.engine.perform_calculation import perform_calculation
def run(mag, nint):
    c = build_default_calc("jwst", "nircam", "sw_imaging")
    src = c["scene"][0]
    src["spectrum"]["normalization"] = {"type": "at_lambda", "norm_wave": 2.0, "norm_waveunit": "um", "norm_flux": mag, "norm_fluxunit": "abmag"}
    src["spectrum"]["sed"] = {"sed_type": "flat", "unit": "fnu"}
    c["configuration"]["instrument"]["filter"] = "f200w"
    c["configuration"]["detector"].update({"readout_pattern": "rapid", "ngroup": 2, "nint": nint, "nexp": 1})
    st = c["strategy"]; st["background_subtraction"] = False; st["aperture_size"] = 1.2; st["sky_annulus"] = [1.4, 1.7]
    for k in ("cosmic_rays_on", "dark_current_on", "readnoise_excess_additive_on", "flat_field_noise_on", "readnoise_on"):
        c["calculation"]["noise"][k] = False
    sc = perform_calculation(c, dict_report=True)["scalar"]
    S, B, T, sn = sc["extracted_flux"], sc["background_total"], sc["measurement_time"], sc["sn"]
    return dict(abmag=mag, nint=nint, source_e_per_s=S, sky_e_per_s=B, measurement_time_s=T, pandeia_sn=sn,
                ara_sn=S * math.sqrt(T) / math.sqrt(S + B), ara_time_for_pandeia_sn=sn * sn * (S + B) / (S * S))
cases = {"source_dominated": run(18.5, 100), "balanced": run(20.74, 200), "sky_limited": run(27.0, 400)}
for v in cases.values():
    v["sn_ratio"] = v["pandeia_sn"] / v["ara_sn"]; v["time_ratio"] = v["ara_time_for_pandeia_sn"] / v["measurement_time_s"]
print(json.dumps(cases, indent=1))
