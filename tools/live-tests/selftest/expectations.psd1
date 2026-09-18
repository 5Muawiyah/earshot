<#
    What each shipped live test must record when it is run against the fake machine.

    The key is "<test id>|<half>". Under it, one entry per set of fake inputs:

      none  every log pattern and every evidence list is empty
      one   exactly one matching line and one item in every list
      two   two of each

    Criteria holds every criterion the run must record, with the outcome the fake inputs
    imply. A criterion that is recorded and is not named here is a failure too, so a branch
    that stops being reached cannot go unnoticed.

    Three rules were used to work these out, and only these:

      * the fake machine starts in the state that test's own preconditions describe, and each
        command moves it the way the real one would (Fakes.psm1, StartStates and
        Update-FakeWorld),
      * the owner answers the way a machine in good order would be answered (Fakes.psm1,
        Answers), and
      * anything counted out of the log or out of an evidence list holds 0, 1 or 2 items
        depending on the case. That is the only difference between the three columns, so a
        criterion whose outcome changes between them is one the counting decides, and those
        are the ones the unrolling defect used to destroy.

    "any" means the criterion must be recorded but its outcome is not this machine's to
    decide. There are three, and all three read something outside the sandbox that cannot be
    redirected: the startup value under the user's own Run key, and the program folder, which
    Windows resets to the real Program Files in every child process whatever the parent set
    ProgramFiles to. Overall may then be one of two outcomes, which is what the list is for.
#>
@{

    # ---------------------------------------------------------------- 00 restore
    # Nothing here is counted out of the log or an evidence list, so all three columns agree.
    '00-restore|first' = @{
        none = @{ Overall = 'pass'; Criteria = @{ 'nodes' = 'pass'; 'protection' = 'pass'; 'block-at-boot' = 'pass'; 'tasks-as-shipped' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'nodes' = 'pass'; 'protection' = 'pass'; 'block-at-boot' = 'pass'; 'tasks-as-shipped' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'nodes' = 'pass'; 'protection' = 'pass'; 'block-at-boot' = 'pass'; 'tasks-as-shipped' = 'pass' } }
    }

    # ------------------------------------------------------------ 01 A2DP one-shot
    # The filters a ks run finds depend on protection, not on the case, and the notification
    # count this test reads is only a finding, so all three columns agree.
    '01-a2dp-oneshot|first' = @{
        none = @{ Overall = 'pass'; Criteria = @{
                'topology-reachable' = 'pass'; 'preconditions' = 'pass'; 'protection-on' = 'pass'
                'K1-src-protected' = 'pass'; 'K1-src-active' = 'pass'; 'K1-budget' = 'pass'
                'K1-wave-absent' = 'pass'; 'K1-all-protected' = 'pass'; 'K1-src-unprotected' = 'pass'
                'K1-wave-brings-a2dp' = 'pass'; 'restored' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{
                'topology-reachable' = 'pass'; 'preconditions' = 'pass'; 'protection-on' = 'pass'
                'K1-src-protected' = 'pass'; 'K1-src-active' = 'pass'; 'K1-budget' = 'pass'
                'K1-wave-absent' = 'pass'; 'K1-all-protected' = 'pass'; 'K1-src-unprotected' = 'pass'
                'K1-wave-brings-a2dp' = 'pass'; 'restored' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{
                'topology-reachable' = 'pass'; 'preconditions' = 'pass'; 'protection-on' = 'pass'
                'K1-src-protected' = 'pass'; 'K1-src-active' = 'pass'; 'K1-budget' = 'pass'
                'K1-wave-absent' = 'pass'; 'K1-all-protected' = 'pass'; 'K1-src-unprotected' = 'pass'
                'K1-wave-brings-a2dp' = 'pass'; 'restored' = 'pass' } }
    }

    # -------------------------------------------------------------- 02 disconnect
    '02-disconnect|first' = @{
        none = @{ Overall = 'pass'; Criteria = @{
                'preconditions' = 'pass'; 'disconnect-src' = 'pass'; 'disconnect-src-unplugged' = 'pass'
                'disconnect-budget' = 'pass'; 'disconnect-wave' = 'pass'; 'disconnect-all' = 'pass'
                'block-recorded' = 'pass'; 'left-allowed' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{
                'preconditions' = 'pass'; 'disconnect-src' = 'pass'; 'disconnect-src-unplugged' = 'pass'
                'disconnect-budget' = 'pass'; 'disconnect-wave' = 'pass'; 'disconnect-all' = 'pass'
                'block-recorded' = 'pass'; 'left-allowed' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{
                'preconditions' = 'pass'; 'disconnect-src' = 'pass'; 'disconnect-src-unplugged' = 'pass'
                'disconnect-budget' = 'pass'; 'disconnect-wave' = 'pass'; 'disconnect-all' = 'pass'
                'block-recorded' = 'pass'; 'left-allowed' = 'pass' } }
    }

    # ------------------------------------------------------------- 03 allow pages
    '03-allow-pages|first' = @{
        none = @{ Overall = 'pass'; Criteria = @{ 'preconditions' = 'pass'; 'allow' = 'pass'; 'no-auto-page' = 'pass'; 'left-blocked' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'preconditions' = 'pass'; 'allow' = 'pass'; 'no-auto-page' = 'pass'; 'left-blocked' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'preconditions' = 'pass'; 'allow' = 'pass'; 'no-auto-page' = 'pass'; 'left-blocked' = 'pass' } }
    }

    # --------------------------------------------------------- 04 block and reboot
    # nodes-present is not recorded: the fake device has target nodes, so that branch is not
    # the one taken.
    '04-block-and-reboot|first' = @{
        none = @{ Overall = 'pass'; Criteria = @{ 'block' = 'pass'; 'disabled-now' = 'pass'; 'locatable' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'block' = 'pass'; 'disabled-now' = 'pass'; 'locatable' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'block' = 'pass'; 'disabled-now' = 'pass'; 'locatable' = 'pass' } }
    }
    '04-block-and-reboot|resume' = @{
        none = @{ Overall = 'pass'; Criteria = @{ 'persisted' = 'pass'; 'stayed-on-phone' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'persisted' = 'pass'; 'stayed-on-phone' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'persisted' = 'pass'; 'stayed-on-phone' = 'pass' } }
    }

    # ------------------------------------------------------------------- 05 allow
    '05-allow|first' = @{
        none = @{ Overall = 'pass'; Criteria = @{ 'allow' = 'pass'; 'problem-cleared' = 'pass'; 'bit-cleared' = 'pass'; 'endpoints-back' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'allow' = 'pass'; 'problem-cleared' = 'pass'; 'bit-cleared' = 'pass'; 'endpoints-back' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'allow' = 'pass'; 'problem-cleared' = 'pass'; 'bit-cleared' = 'pass'; 'endpoints-back' = 'pass' } }
    }
    '05-allow|resume' = @{
        none = @{ Overall = 'pass'; Criteria = @{ 'still-allowed' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'still-allowed' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'still-allowed' = 'pass' } }
    }

    # --------------------------------------------------------------- 06 Handsfree
    # Two criteria here are counted, and they are the reason this test is in the self-test:
    # unelevated-recorded counts the steps in the diag evidence, and
    # services-readable-while-blocked counts the services the probe listed while the nodes
    # were disabled. With nothing to count, the first is inconclusive and the second fails,
    # which is the honest answer and not a throw.
    '06-handsfree|first' = @{
        none = @{ Overall = 'fail'; Criteria = @{
                'unelevated-recorded' = 'inconclusive'; 'gate-protect-on' = 'pass'; 'within-task-limit' = 'pass'
                'protection-survives-reconnect' = 'pass'; 'services-readable-while-blocked' = 'fail'; 'left-as-asked' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{
                'unelevated-recorded' = 'pass'; 'gate-protect-on' = 'pass'; 'within-task-limit' = 'pass'
                'protection-survives-reconnect' = 'pass'; 'services-readable-while-blocked' = 'pass'; 'left-as-asked' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{
                'unelevated-recorded' = 'pass'; 'gate-protect-on' = 'pass'; 'within-task-limit' = 'pass'
                'protection-survives-reconnect' = 'pass'; 'services-readable-while-blocked' = 'pass'; 'left-as-asked' = 'pass' } }
    }

    # --------------------------------------------------------------- 07 task RunEx
    # planb is not recorded: the fake tasks may be started from this token and the gate run
    # completes, so plan B is not reached.
    '07-task-runex|first' = @{
        none = @{ Overall = 'pass'; Criteria = @{
                'setup' = 'pass'; 'ace-present' = 'pass'; 'runex' = 'pass'; 'arguments-arrive' = 'pass'
                'lasttaskresult' = 'pass'; 'setboot-round-trip' = 'pass'; 'setboot-restored' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{
                'setup' = 'pass'; 'ace-present' = 'pass'; 'runex' = 'pass'; 'arguments-arrive' = 'pass'
                'lasttaskresult' = 'pass'; 'setboot-round-trip' = 'pass'; 'setboot-restored' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{
                'setup' = 'pass'; 'ace-present' = 'pass'; 'runex' = 'pass'; 'arguments-arrive' = 'pass'
                'lasttaskresult' = 'pass'; 'setboot-round-trip' = 'pass'; 'setboot-restored' = 'pass' } }
    }

    # ------------------------------------------------- 08 acceptance power cycle
    '08-acceptance-power-cycle|first' = @{
        none = @{ Overall = 'pass'; Criteria = @{ 'default-config' = 'pass'; 'blocked-before-power-cycle' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'default-config' = 'pass'; 'blocked-before-power-cycle' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'default-config' = 'pass'; 'blocked-before-power-cycle' = 'pass' } }
    }
    # This is the half the defect used to destroy. The throw was on the "connected at
    # start-up" count, and it took the whole left-click half with it: left-click-connects,
    # no-admin-prompt, left-click-disconnects, no-admin-prompt-disconnect and
    # blocked-again-after-click, which are definition-of-done item 2. All five are named here,
    # in all three columns, so the run has to reach them whatever the log holds. clean-exit is
    # the one this half counts: no clean stop line in the log leaves it inconclusive.
    '08-acceptance-power-cycle|resume' = @{
        none = @{ Overall = 'inconclusive'; Criteria = @{
                'ACCEPTANCE' = 'pass'; 'still-blocked' = 'pass'; 'no-fresh-handsfree-node' = 'pass'; 'not-taken-by-pc' = 'pass'
                'left-click-connects' = 'pass'; 'no-admin-prompt' = 'pass'; 'click-agrees-with-endpoints' = 'pass'
                'left-click-disconnects' = 'pass'; 'no-admin-prompt-disconnect' = 'pass'
                'disconnect-agrees-with-endpoints' = 'pass'; 'blocked-again-after-click' = 'pass'
                'icon-clean' = 'pass'; 'icon-dpi' = 'pass'; 'icon-theme' = 'pass'; 'card-no-focus' = 'pass'
                'click-once' = 'pass'; 'menu' = 'pass'; 'startup-value' = 'any'; 'startup-agrees' = 'pass'
                'single-instance' = 'pass'; 'clean-exit' = 'inconclusive' } }
        one  = @{ Overall = @('pass', 'inconclusive'); Criteria = @{
                'ACCEPTANCE' = 'pass'; 'still-blocked' = 'pass'; 'no-fresh-handsfree-node' = 'pass'; 'not-taken-by-pc' = 'pass'
                'left-click-connects' = 'pass'; 'no-admin-prompt' = 'pass'; 'click-agrees-with-endpoints' = 'pass'
                'left-click-disconnects' = 'pass'; 'no-admin-prompt-disconnect' = 'pass'
                'disconnect-agrees-with-endpoints' = 'pass'; 'blocked-again-after-click' = 'pass'
                'icon-clean' = 'pass'; 'icon-dpi' = 'pass'; 'icon-theme' = 'pass'; 'card-no-focus' = 'pass'
                'click-once' = 'pass'; 'menu' = 'pass'; 'startup-value' = 'any'; 'startup-agrees' = 'pass'
                'single-instance' = 'pass'; 'clean-exit' = 'pass' } }
        two  = @{ Overall = @('pass', 'inconclusive'); Criteria = @{
                'ACCEPTANCE' = 'pass'; 'still-blocked' = 'pass'; 'no-fresh-handsfree-node' = 'pass'; 'not-taken-by-pc' = 'pass'
                'left-click-connects' = 'pass'; 'no-admin-prompt' = 'pass'; 'click-agrees-with-endpoints' = 'pass'
                'left-click-disconnects' = 'pass'; 'no-admin-prompt-disconnect' = 'pass'
                'disconnect-agrees-with-endpoints' = 'pass'; 'blocked-again-after-click' = 'pass'
                'icon-clean' = 'pass'; 'icon-dpi' = 'pass'; 'icon-theme' = 'pass'; 'card-no-focus' = 'pass'
                'click-once' = 'pass'; 'menu' = 'pass'; 'startup-value' = 'any'; 'startup-agrees' = 'pass'
                'single-instance' = 'pass'; 'clean-exit' = 'pass' } }
    }

    # --------------------------------------------- 09 shutdown while connected
    '09-shutdown-while-connected|first' = @{
        none = @{ Overall = 'pass'; Criteria = @{ 'connected-first' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'connected-first' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'connected-first' = 'pass' } }
    }
    # end-session-logged counts the query and end lines, so it is a fail when the log holds
    # neither. That is the answer, and the run still reaches the two criteria after it.
    '09-shutdown-while-connected|resume' = @{
        none = @{ Overall = 'fail'; Criteria = @{ 'not-paged-at-boot' = 'pass'; 'nodes-after-boot' = 'pass'; 'end-session-logged' = 'fail' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'not-paged-at-boot' = 'pass'; 'nodes-after-boot' = 'pass'; 'end-session-logged' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'not-paged-at-boot' = 'pass'; 'nodes-after-boot' = 'pass'; 'end-session-logged' = 'pass' } }
    }

    # ------------------------------------------------------- 10 shutdown messages
    # The first half records no criterion at all: it reads the state, writes the marker and
    # prints the resume line. A run with nothing recorded is inconclusive, which is right.
    '10-shutdown-messages-v1|first' = @{
        none = @{ Overall = 'inconclusive'; Criteria = @{ } }
        one  = @{ Overall = 'inconclusive'; Criteria = @{ } }
        two  = @{ Overall = 'inconclusive'; Criteria = @{ } }
    }
    # Every criterion in this half is a count out of the log, taken from the marker time the
    # first half wrote, so all three move together.
    '10-shutdown-messages-v1|resume' = @{
        none = @{ Overall = 'fail'; Criteria = @{ 'query-arrived' = 'fail'; 'end-arrived' = 'fail'; 'block-queued' = 'inconclusive' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'query-arrived' = 'pass'; 'end-arrived' = 'pass'; 'block-queued' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'query-arrived' = 'pass'; 'end-arrived' = 'pass'; 'block-queued' = 'pass' } }
    }

    # ------------------------------------------------------ 11 battery disconnected
    # The two battery criteria pass when nothing came back, so this is the one test whose
    # empty case is the good one. leg1-preconditions is not recorded: the fake AirPods start
    # off this PC, which is what leg 1 needs.
    '11-battery-disconnected|first' = @{
        none = @{ Overall = 'pass'; Criteria = @{ 'sweep-disconnected' = 'pass'; 'no-battery-disconnected' = 'pass'; 'sweep-connected' = 'pass' } }
        one  = @{ Overall = 'fail'; Criteria = @{ 'sweep-disconnected' = 'pass'; 'no-battery-disconnected' = 'fail'; 'sweep-connected' = 'fail' } }
        two  = @{ Overall = 'fail'; Criteria = @{ 'sweep-disconnected' = 'pass'; 'no-battery-disconnected' = 'fail'; 'sweep-connected' = 'fail' } }
    }

    # ---------------------------------------------------------- 12 callback thread
    '12-callback-thread|first' = @{
        none = @{ Overall = 'fail'; Criteria = @{ 'notifications-arrive' = 'fail'; 'apartment' = 'inconclusive'; 'protection-churn' = 'pass'; 'clean-exit' = 'inconclusive' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'notifications-arrive' = 'pass'; 'apartment' = 'pass'; 'protection-churn' = 'pass'; 'clean-exit' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'notifications-arrive' = 'pass'; 'apartment' = 'pass'; 'protection-churn' = 'pass'; 'clean-exit' = 'pass' } }
    }

    # ------------------------------------------------------------- 13 grace window
    # This test threw on its very first criterion, so integrator decision D1, the thirty
    # second idle grace, could not be settled by any sitting. All three criteria are named in
    # all three columns. not-too-long is the counted one: with no block line in the log the
    # rule never fired inside the watch, and inconclusive is the honest answer.
    '13-grace-window|first' = @{
        none = @{ Overall = 'inconclusive'; Criteria = @{ 'not-too-short' = 'pass'; 'blocks-when-idle' = 'pass'; 'not-too-long' = 'inconclusive' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'not-too-short' = 'pass'; 'blocks-when-idle' = 'pass'; 'not-too-long' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'not-too-short' = 'pass'; 'blocks-when-idle' = 'pass'; 'not-too-long' = 'pass' } }
    }

    # ------------------------------------------------------ 14 set-device refusal
    # refuses-protected-move counts the services protection.json records as turned off, so it
    # is inconclusive when the record is empty and there is nothing for the guard to protect.
    # phone-address is not recorded: an address was available, so that branch is not taken.
    '14-set-device-refusal|first' = @{
        none = @{ Overall = 'inconclusive'; Criteria = @{
                'refuses-phone' = 'pass'; 'device-file-unchanged' = 'pass'; 'phone-untouched' = 'pass'
                'refuses-protected-move' = 'inconclusive'; 'picker-lists-devices' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{
                'refuses-phone' = 'pass'; 'device-file-unchanged' = 'pass'; 'phone-untouched' = 'pass'
                'refuses-protected-move' = 'pass'; 'picker-lists-devices' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{
                'refuses-phone' = 'pass'; 'device-file-unchanged' = 'pass'; 'phone-untouched' = 'pass'
                'refuses-protected-move' = 'pass'; 'picker-lists-devices' = 'pass' } }
    }

    # ------------------------------------------------------ 15 uninstall reversal
    # program-folder and delayed-deletion read Program Files, which no parent can redirect,
    # so only that they are recorded is proven here.
    '15-uninstall-reversal|first' = @{
        none = @{ Overall = @('pass', 'fail'); Criteria = @{
                'uninstall' = 'pass'; 'nodes-restored' = 'pass'; 'services-restored' = 'pass'; 'tasks-removed' = 'pass'
                'data-folder-removed' = 'pass'; 'program-folder' = 'any'; 'install-again' = 'pass' } }
        one  = @{ Overall = @('pass', 'fail'); Criteria = @{
                'uninstall' = 'pass'; 'nodes-restored' = 'pass'; 'services-restored' = 'pass'; 'tasks-removed' = 'pass'
                'data-folder-removed' = 'pass'; 'program-folder' = 'any'; 'install-again' = 'pass' } }
        two  = @{ Overall = @('pass', 'fail'); Criteria = @{
                'uninstall' = 'pass'; 'nodes-restored' = 'pass'; 'services-restored' = 'pass'; 'tasks-removed' = 'pass'
                'data-folder-removed' = 'pass'; 'program-folder' = 'any'; 'install-again' = 'pass' } }
    }
    '15-uninstall-reversal|resume' = @{
        none = @{ Overall = @('pass', 'fail'); Criteria = @{ 'delayed-deletion' = 'any' } }
        one  = @{ Overall = @('pass', 'fail'); Criteria = @{ 'delayed-deletion' = 'any' } }
        two  = @{ Overall = @('pass', 'fail'); Criteria = @{ 'delayed-deletion' = 'any' } }
    }
}
