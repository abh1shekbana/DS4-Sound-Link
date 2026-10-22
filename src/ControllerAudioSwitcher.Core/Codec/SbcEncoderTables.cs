namespace ControllerAudioSwitcher.Core.Codec;

/// <summary>
/// Windowing coefficient tables and analysis filter matrix for SBC encoder.
/// Reference: Bluetooth A2DP specification, SBC Appendix B, Table 8.24.
/// </summary>
internal static class SbcEncoderTables
{
    private static readonly double[] Prototype8 =
    {
        0.00000000E+00,  1.56575398E-04,  3.43256425E-04,  5.54620202E-04,
        8.23919506E-04,  1.13992507E-03,  1.47640169E-03,  1.78371725E-03,
        2.01182542E-03,  2.10371989E-03,  1.99454554E-03,  1.61656283E-03,
        9.02154502E-04, -1.78805361E-04, -1.64973098E-03, -3.49717454E-03,
        5.65949473E-03,  8.02941163E-03,  1.04584443E-02,  1.27472335E-02,
        1.46525263E-02,  1.59045603E-02,  1.62208471E-02,  1.53184106E-02,
        1.29371806E-02,  8.85757540E-03,  2.92408442E-03, -4.91578024E-03,
       -1.46404076E-02, -2.61098752E-02, -3.90751381E-02, -5.31873032E-02,
        6.79989431E-02,  8.29847578E-02,  9.75753918E-02,  1.11196689E-01,
        1.23264548E-01,  1.33264415E-01,  1.40753505E-01,  1.45389847E-01,
        1.46955068E-01,  1.45389847E-01,  1.40753505E-01,  1.33264415E-01,
        1.23264548E-01,  1.11196689E-01,  9.75753918E-02,  8.29847578E-02,
       -6.79989431E-02, -5.31873032E-02, -3.90751381E-02, -2.61098752E-02,
       -1.46404076E-02, -4.91578024E-03,  2.92408442E-03,  8.85757540E-03,
        1.29371806E-02,  1.53184106E-02,  1.62208471E-02,  1.59045603E-02,
        1.46525263E-02,  1.27472335E-02,  1.04584443E-02,  8.02941163E-03,
       -5.65949473E-03, -3.49717454E-03, -1.64973098E-03, -1.78805361E-04,
        9.02154502E-04,  1.61656283E-03,  1.99454554E-03,  2.10371989E-03,
        2.01182542E-03,  1.78371725E-03,  1.47640169E-03,  1.13992507E-03,
        8.23919506E-04,  5.54620202E-04,  3.43256425E-04,  1.56575398E-04,
    };

    public static readonly double[][] AnalysisMatrix8 = BuildAnalysisMatrix8();

    private static double[][] BuildAnalysisMatrix8()
    {
        const int subbands = 8;
        var result = new double[subbands][];
        for (int band = 0; band < subbands; band++)
        {
            result[band] = new double[Prototype8.Length];
            for (int tap = 0; tap < Prototype8.Length; tap++)
            {
                double prototype = Prototype8[tap] * ((((tap / 16) & 1) == 0) ? 1.0 : -1.0);
                result[band][tap] = prototype * Math.Cos((band + 0.5) * (tap - subbands / 2.0) * Math.PI / subbands);
            }
        }
        return result;
    }
}
