import copy
import unittest
import numpy as np
import run_score_adaptation as run


def fixture():
    return {
        'a': {'features':np.array([[0.],[1.]]),'train_mass':np.array([2.,2.]),'train_positive_mass':np.array([0.,2.])},
        'a_alias': {'features':np.array([[2.]]),'train_mass':np.array([4.]),'train_positive_mass':np.array([4.])},
        'b': {'features':np.array([[10.],[11.]]),'train_mass':np.array([.1,.9]),'train_positive_mass':np.array([0.,0.])},
        'c': {'features':np.array([[100.]]),'train_mass':np.array([30.]),'train_positive_mass':np.array([30.])},
    }


class RunnerTests(unittest.TestCase):
    def test_source_equalization_and_test_isolation(self):
        records=fixture(); parents={'a':'one','a_alias':'one','b':'two','c':'held_out'}
        x,y,w,audit=run.assemble_training(records,['a','a_alias','b'],parents)
        self.assertAlmostEqual(w[:3].sum(),1.)
        self.assertAlmostEqual(w[3:].sum(),1.)
        changed=copy.deepcopy(records); changed['c']['features'][:]=1e12
        changed['c']['train_mass'][:]=1e6;changed['c']['train_positive_mass'][:]=0
        x2,y2,w2,_=run.assemble_training(changed,['a','a_alias','b'],parents)
        np.testing.assert_array_equal(x,x2);np.testing.assert_array_equal(y,y2);np.testing.assert_array_equal(w,w2)
        self.assertNotIn('held_out',audit['parent_physical_seconds'])

    def test_same_pcm_parent_cannot_cross_fold(self):
        records=fixture();parents={'a':'one','a_alias':'one','b':'two','c':'three'}
        groups={'bad':['a'],'other':['a_alias','b','c']}
        with self.assertRaisesRegex(ValueError,'shared_parent_crosses_split'):
            run.validate_folds(groups,parents,records)

    def test_song_positive_holdout_detects_one_class(self):
        records=fixture();parents={key:key for key in records}
        groups={'positive_content':['a','a_alias','c'],'negative':['b']}
        folds=run.validate_folds(groups,parents,records)
        self.assertFalse(folds[0]['two_class_training_eligible'])
        self.assertTrue(folds[1]['two_class_training_eligible'])

    def test_native_cell_partial_known_mass_and_soft_label(self):
        known=np.array([.5,.5,0,0,1,1],dtype=float)
        positive=np.array([0,.5,0,0,1,0],dtype=float)
        np.testing.assert_array_equal(run.cell_sum(known,[0,3,6]),[1.,2.])
        np.testing.assert_array_equal(run.cell_sum(positive,[0,3,6]),[.5,1.])

    def test_half_sample_units_keep_long_pure_positive_targets_exact(self):
        units=np.full(305120,2.,dtype=float); positive=units.copy()
        positive[:55201]=0;edges=list(range(0,len(units),640))+[len(units)]
        known=run.cell_sum(units,edges)/(2*16000)
        yes=run.cell_sum(positive,edges)/(2*16000)
        targets=yes/known
        self.assertTrue((targets<=1).all());self.assertTrue((targets>=0).all())
        self.assertTrue((targets[90:]==1).all())


if __name__=='__main__':unittest.main()
