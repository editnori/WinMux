# Question Catalog Coverage

This report separates three things:
- clinician-intent catalog entries (initially seeded from workbook import)
- event-first extraction families and event types
- how saved event artifacts currently answer questions

Ontology material is reference/governance input. Extraction truth lives in the saved evidence-backed event artifacts.

## Inventory

- Total clinician-intent catalog entries: **216**
- `Sheet1` note-oriented clinician questions: **130**
- Structured data clinician-intent entries: **86**
- Exact unique `Sheet1` question texts: **108**
- Canonical `Sheet1` question keys: **107**
- Event types with no current question link: **0**

## Current Status

- `answered_from_events`: **112**
- `answered_from_timeline`: **18**

## Duplicate Raw Questions

- **Did the patient experience urinary retention?** x2
- **Has the patient been pregnant?** x2
- **How was the patient positioned?** x2
- **Is the patient on enteral nutriton?** x2
- **Was DISS used?** x2
- **Was a hematoma present after surgery** x2
- **Was a ramp up energy strategy used?** x2
- **Was a safety wire used?** x2
- **What is the education level of the patient?** x2
- **What is the profession of the patient?** x2
- **What was the diameter of the balloon dilation?** x2
- **What was the diameter of the stent?** x2
- **What was the laser fiber size?** x2
- **What was the laser fiber type?** x3
- **What was the laser setting frequency?** x2
- **What was the laser setting power?** x3
- **What was the length of the stent?** x2
- **What was the make / model of ureteroscope?** x2
- **What was the ureteroscope type?** x2
- **Which family members have a history of kidney stones?** x2

## Event Bundle Map

### `history_timeline -> history_fact`

- Questions in bundle: **22**
- Observed notes in artifact corpus: **120**
- Observed events in artifact corpus: **206**
- Top observed terms: `kidney stone history` (91), `horseshoe kidney` (33), `prior nephrostomy` (19), `prior ureteral stent` (17), `prior ureteroscopy` (15)
- `answered_from_events`: 4
- `answered_from_timeline`: 18

- `answered_from_events` Has the patient been pregnant?
- `answered_from_events` Has the patient been pregnant?
- `answered_from_events` How many kidney stones has the patient passed?
- `answered_from_events` What anatomic variants does the patient have?
- `answered_from_timeline` Did the patient experience an obstetric complication?
- `answered_from_timeline` Has the patient ever had a kidney stone before index visit?
  Observed answers: `True` (18)
- `answered_from_timeline` Has the patient ever had a ureteral stent?
  Observed answers: `True` (3)
- `answered_from_timeline` Has the patient ever had kidney stone surgery prior to the index visit?
  Observed answers: `True` (7)
- `answered_from_timeline` Has the patient ever passed a stone prior to the index visit?
  Observed answers: `True` (4)
- `answered_from_timeline` Has the patient had MRDO UTIs?
- `answered_from_timeline` Has the patient had a prior UTI?
  Observed answers: `True` (1)
- `answered_from_timeline` Has the patient had any complications during pregnancy?
- `answered_from_timeline` Has the patient had kidney stones during pregnancy?
- `answered_from_timeline` Has the patient had recurrent UTIs?
  Observed answers: `True` (1)
- `answered_from_timeline` How many prior stone surgeries were performed (of each type)
  Observed answers: `[{"count": 1, "value": "prior lithotripsy"}]` (3), `[{"count": 3, "value": "prior nephrostomy"}, {"count": 3, "value": "prior ureteroscopy"}]` (1), `[{"count": 2, "value": "prior ureteroscopy"}, {"count": 1, "value": "prior percutaneous nephrolithotomy"}]` (1), `[{"count": 16, "value": "prior nephrostomy"}, {"count": 9, "value": "prior ureteroscopy"}]` (1), `[{"count": 1, "value": "prior ureteroscopy"}]` (1)
- `answered_from_timeline` How many stones has the patient had prior to the index visit?
  Observed answers: `["multiple"]` (2), `[2, "2"]` (1), `["20+"]` (1), `[5]` (1)
- `answered_from_timeline` Is there a family history of kidney stones?
  Observed answers: `True` (3)
- `answered_from_timeline` What urologic surgeries have the patients recieved prior to the index visit?
  Observed answers: `["prior lithotripsy"]` (3), `["prior nephrostomy", "prior ureteroscopy"]` (2), `["prior percutaneous nephrolithotomy", "prior ureteroscopy"]` (1), `["prior ureteroscopy"]` (1)
- `answered_from_timeline` What was the age of onset of first stone prior to the index visit?
- `answered_from_timeline` What was the prior stone surgery type prior to the index visit?
  Observed answers: `["prior lithotripsy"]` (3), `["prior nephrostomy", "prior ureteroscopy"]` (2), `["prior percutaneous nephrolithotomy", "prior ureteroscopy"]` (1), `["prior ureteroscopy"]` (1)
- `answered_from_timeline` Which family members have a history of kidney stones?
  Observed answers: `["mother"]` (2), `["father"]` (1)
- `answered_from_timeline` Which family members have a history of kidney stones?
  Observed answers: `["mother"]` (2), `["father"]` (1)

### `history_timeline -> social_context_fact`

- Questions in bundle: **11**
- Observed notes in artifact corpus: **33**
- Observed events in artifact corpus: **43**
- Top observed terms: `caregiver support` (17), `lives alone` (13), `education level` (6), `transportation available` (3), `employed` (2)
- `answered_from_events`: 11

- `answered_from_events` Does the patient have food insecurity?
- `answered_from_events` Does the patient have transportation?
- `answered_from_events` Is the patient on enteral nutriton?
- `answered_from_events` Is the patient on enteral nutriton?
- `answered_from_events` Is there a caregiver that helps at home?
- `answered_from_events` What is the education level of the patient?
  Observed answers: `["some college"]` (1), `["MTSU grad with degree - Recording Industry related"]` (1), `["11th grade; St. Cecelia; in person"]` (1), `["11th grade - St. Cecelia. In person"]` (1), `["Freshman 2022/23 Tennessee Tech"]` (1)
- `answered_from_events` What is the education level of the patient?
- `answered_from_events` What is the patient's frailty index?
- `answered_from_events` What is the profession of the patient?
  Observed answers: `["setting up lights for events"]` (1)
- `answered_from_events` What is the profession of the patient?
- `answered_from_events` What type of tube feeds are used?

### `medications -> medication_exposure`

- Questions in bundle: **7**
- Observed notes in artifact corpus: **205**
- Observed events in artifact corpus: **535**
- Top observed terms: `ceftriaxone` (60), `ondansetron` (52), `tamsulosin` (38), `acetaminophen` (36), `oxycodone` (26)
- `answered_from_events`: 7

- `answered_from_events` What medication was given?
  Observed answers: `["ceftriaxone"]` (22), `["tamsulosin"]` (17), `["allopurinol"]` (11), `["meropenem"]` (10), `["ceftriaxone", "oxycodone"]` (7)
- `answered_from_events` What was the context of medication administration (i.e. at home, intra-operatively, pre-op)
  Observed answers: `["AT HOME"]` (52), `["ED"]` (25), `["INPATIENT"]` (12), `[null]` (10), `["DISCHARGE"]` (7)
- `answered_from_events` What was the date of administration / prescription?
  Observed answers: `["2018-05-05"]` (2), `["03/14/21"]` (1), `["2020-12-16", "2020-12-15"]` (1), `["2021-03-19"]` (1), `["2021-03-14"]` (1)
- `answered_from_events` What was the dose of medication
  Observed answers: `["0.4 mg"]` (21), `["2000 mg"]` (13), `["1000 mg"]` (11), `["100 mg"]` (8), `["2000 mg", "2.5 mg"]` (7)
- `answered_from_events` What was the duration of the medication?
  Observed answers: `["14-day course"]` (8), `["14 day course"]` (3), `["7 day course"]` (2), `["3 days"]` (2), `["x1"]` (1)
- `answered_from_events` What was the frequency of dosing (i.e. once a day; twice a day)
  Observed answers: `["daily"]` (45), `["nightly"]` (19), `["daily", "q6h"]` (13), `["q12h"]` (13), `["q6h"]` (10)
- `answered_from_events` What was the route of medication administration
  Observed answers: `["PO"]` (65), `["IV"]` (54), `["IV", "PO"]` (32), `["PO", "IV"]` (25), `["PO", "rectal"]` (1)

### `outcomes_complications -> adverse_effect`

- Questions in bundle: **9**
- Observed notes in artifact corpus: **166**
- Observed events in artifact corpus: **279**
- Top observed terms: `acute kidney injury` (78), `sepsis` (48), `bacteremia` (39), `urosepsis` (34), `pyelonephritis` (26)
- `answered_from_events`: 9

- `answered_from_events` Did the patient experience an anesthetic complication?
- `answered_from_events` Did the patient experience urinary retention?
- `answered_from_events` Did the patient experience urinary retention?
- `answered_from_events` Was a hematoma present after surgery
  Observed answers: `True` (1)
- `answered_from_events` Was a hematoma present after surgery
  Observed answers: `True` (1)
- `answered_from_events` Was a urine leak present after surgery?
- `answered_from_events` Was post-operative obstruction present?
- `answered_from_events` Was there an intra-operative ureteral injury?
- `answered_from_events` What medical side effects were encountered?
  Observed answers: `["urosepsis"]` (21), `["sepsis"]` (18), `["bacteremia"]` (12), `["bacteremia", "sepsis"]` (10), `["nausea and vomiting"]` (8)

### `outcomes_complications -> outcome_event`

- Questions in bundle: **3**
- Observed notes in artifact corpus: **121**
- Observed events in artifact corpus: **140**
- Top observed terms: `stone passage` (40), `renal function improved` (24), `symptom resolution` (23), `pain controlled` (18), `culture negative` (10)
- `answered_from_events`: 3

- `answered_from_events` Did the patient experience stone passage?
- `answered_from_events` Did the patient's symptoms resolve after treatment?
- `answered_from_events` Was the patient stone free or were there residual fragments after treatment?

### `outcomes_complications -> recommendation_event`

- Questions in bundle: **3**
- Observed notes in artifact corpus: **91**
- Observed events in artifact corpus: **135**
- Top observed terms: `urology follow-up` (44), `repeat imaging` (25), `return precautions` (13), `urology referral` (11), `hydration` (10)
- `answered_from_events`: 3

- `answered_from_events` Was increased physical activity recommended?
- `answered_from_events` What was the baseline diet?
- `answered_from_events` What was the recommended diet change?

### `procedure_devices -> device_event`

- Questions in bundle: **19**
- Observed notes in artifact corpus: **227**
- Observed events in artifact corpus: **267**
- Top observed terms: `nephrostomy tube` (151), `ureteral stent` (99), `foley catheter` (13), `surgical drain` (3), `ureteral catheter` (1)
- `answered_from_events`: 19

- `answered_from_events` Was a dual lumen catheter used
- `answered_from_events` Was a post-operative stent left?
- `answered_from_events` Was a safety wire used?
- `answered_from_events` Was a safety wire used?
- `answered_from_events` Was a ureteral access sheath used?
- `answered_from_events` Was the stent left on a string/tether?
- `answered_from_events` What was the diameter of the UAS?
- `answered_from_events` What was the diameter of the stent?
- `answered_from_events` What was the diameter of the stent?
- `answered_from_events` What was the length of the UAS?
- `answered_from_events` What was the length of the stent?
- `answered_from_events` What was the length of the stent?
- `answered_from_events` What was the make / model of ureteroscope?
- `answered_from_events` What was the make / model of ureteroscope?
- `answered_from_events` What was the size of the ureteroscope?
- `answered_from_events` What was the type of stent used?
- `answered_from_events` What was the ureteroscope type?
- `answered_from_events` What was the ureteroscope type?
- `answered_from_events` What was used for drainage at the end of the case?

### `procedure_devices -> procedure_event`

- Questions in bundle: **47**
- Observed notes in artifact corpus: **239**
- Observed events in artifact corpus: **366**
- Top observed terms: `nephrostomy placement` (143), `ureteral stent placement` (56), `ureteroscopy` (43), `cystoscopy` (32), `laser lithotripsy` (15)
- `answered_from_events`: 47

- `answered_from_events` How many tracts were used?
- `answered_from_events` How was the patient positioned?
- `answered_from_events` How was the patient positioned?
- `answered_from_events` Was CVAC used?
- `answered_from_events` Was DISS used?
- `answered_from_events` Was DISS used?
- `answered_from_events` Was ECIRS used?
- `answered_from_events` Was FANS used?
- `answered_from_events` Was a 2 minute pause used?
- `answered_from_events` Was a ramp up energy strategy used?
- `answered_from_events` Was a ramp up energy strategy used?
- `answered_from_events` Was a retrograde pyelogram performed?
- `answered_from_events` Was a retroperc used?
- `answered_from_events` Was balloon dilation used?
- `answered_from_events` Was coaxial dilation used?
- `answered_from_events` Was genetic testing for kidney stones performed?
- `answered_from_events` Was suction used?
- `answered_from_events` Was the URS aborted due to inability to access the ureter?
- `answered_from_events` What irrigation type was used?
- `answered_from_events` What was the PCNL tract size?
- `answered_from_events` What was the diameter of coaxial dilation?
- `answered_from_events` What was the diameter of the balloon dilation?
- `answered_from_events` What was the diameter of the balloon dilation?
- `answered_from_events` What was the diameter of the dual lumen?
- `answered_from_events` What was the genetic testing result?
- `answered_from_events` What was the irrigation method?
- `answered_from_events` What was the irrigation pressure?
- `answered_from_events` What was the laser fiber size?
- `answered_from_events` What was the laser fiber size?
- `answered_from_events` What was the laser fiber type?
- `answered_from_events` What was the laser fiber type?
- `answered_from_events` What was the laser fiber type?
- `answered_from_events` What was the laser setting frequency?
- `answered_from_events` What was the laser setting frequency?
- `answered_from_events` What was the laser setting power?
- `answered_from_events` What was the laser setting power?
- `answered_from_events` What was the laser setting power?
- `answered_from_events` What was the lithotripsy technique?
- `answered_from_events` What was the lithotripter energy source?
- `answered_from_events` What was the maximum energy?
- `answered_from_events` What was the shock rate used?
- `answered_from_events` What was the suction pressure?
- `answered_from_events` What was the total energy?
- `answered_from_events` What was the type of SWL machine?
- `answered_from_events` What was the type of laser used?
- `answered_from_events` What were the findings of the retrograde pyelogram?
- `answered_from_events` Which service obtained percutaneous access?

### `symptoms_imaging -> imaging_finding`

- Questions in bundle: **7**
- Observed notes in artifact corpus: **516**
- Observed events in artifact corpus: **745**
- Top observed terms: `hydronephrosis` (290), `kidney stone` (264), `ureteral stone` (80), `hydroureteronephrosis` (49), `hydroureter` (25)
- `answered_from_events`: 7

- `answered_from_events` Is hydronephrosis present?
  Observed answers: `True` (227)
- `answered_from_events` Is the stone a staghorn calculus?
- `answered_from_events` Was hydronephrosis present?
  Observed answers: `True` (227)
- `answered_from_events` What imaging modality was used to guide access?
- `answered_from_events` What is the Hounsfield unit for the stone?
- `answered_from_events` What is the laterality of the stone?
- `answered_from_events` Where is the stone (stone location)?
  Observed answers: `["kidney"]` (102), `["UVJ"]` (20), `["bilateral kidneys"]` (20), `["distal ureter"]` (17), `["ureter"]` (14)

### `symptoms_imaging -> symptom_presentation`

- Questions in bundle: **2**
- Observed notes in artifact corpus: **174**
- Observed events in artifact corpus: **188**
- Top observed terms: `stone-related symptomatic presentation` (188)
- `answered_from_events`: 2

- `answered_from_events` Was the patient symptomatic from this stone
  Observed answers: `True` (142)
- `answered_from_events` What were the patient's symptoms?
  Observed answers: `["HEMATURIA"]` (14), `["RIGHT FLANK PAIN"]` (9), `["ANURIA", "DECREASED URINE OUTPUT"]` (9), `["FLANK PAIN"]` (8), `["DECREASED URINE OUTPUT"]` (6)

## Event Types With No Current Question Link

- None

## Structured Data Rows

These are structured data variables (age, labs, vitals, etc.) - not extracted from clinical notes.

- Age
- Sex
- Race
- Ethnicity
- Height / Weight / BMI
- Blood pressure
- Tobacco Use
- Smoking Hisotry
- Insurance
- Address
- Zip code
- Census Tract
- Health Care utilization (i..e primary care access)
- Medical Comorbities
- ASA Class
- PMCA
- Adjusted Clinical Group Dx-PM Score
- BMP
- Serum Calcium
- Serum PTH
- Serum Uric Acid
- Serum Magnesium
- Serum Phosphorous
- Serum Vitamin D
- Serum oxalate
- ... and 61 more
